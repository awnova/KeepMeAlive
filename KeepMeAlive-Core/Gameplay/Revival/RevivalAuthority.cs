//====================[ Imports ]====================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivalAuthority ]====================
    // Client side of the server revive authority (/keepmealive/state/*).
    //
    // SPT's RequestHandler is synchronous, so every call runs off the Unity main thread. All calls
    // share ONE ordered background queue: a later request (e.g. request-revive-start) can never
    // overtake an earlier one (e.g. begin-critical) and be rejected for arriving first.
    // Results are handed back as Tasks; callers poll them from a coroutine and apply results on
    // the main thread.
    //
    // Every call is tagged with the raid generation it was queued in. One still waiting in the
    // queue when the raid ends is dropped unsent: the server resets its revive state at raid
    // start and end, so an earlier raid's request landing after that would corrupt the new raid.
    //
    // Lifecycle notifications (begin-critical, complete-revive, ...) are retried in place until the
    // server answers or the raid ends: a lost begin-critical leaves the server thinking the player
    // is not downed (every revive denied), a lost complete-revive leaves it stuck in Reviving with
    // the life never charged. Retrying inside the queue keeps everything behind them in order.
    internal static class RevivalAuthority
    {
        //====================[ Constants ]====================
        private const string BaseRoute = "/keepmealive/state";
        private const int CompleteInvalidStateCode = 4;
        private const int RevivedStateCode = 3;
        private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000, 8000 };
        private const int MaxRetryDelayMs = 10000;

        //====================[ Network Models ]====================
        private sealed class AuthorityRequest
        {
            public string PlayerId { get; set; } = string.Empty;
            public string ReviverId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
        }

        private sealed class AuthorityStateDto
        {
            public int State { get; set; }
            public int LivesRemaining { get; set; }
        }

        private sealed class AuthorityResponse
        {
            public bool Success { get; set; }
            public int DenialCode { get; set; }
            public string Reason { get; set; } = string.Empty;
            public AuthorityStateDto State { get; set; }
        }

        internal readonly struct AuthorizeResult
        {
            public readonly bool Allowed;
            public readonly string Reason;

            public AuthorizeResult(bool allowed, string reason)
            {
                Allowed = allowed;
                Reason = reason ?? string.Empty;
            }
        }

        //====================[ Ordered Queue ]====================
        private static readonly object QueueLock = new();
        private static Task _queueTail = Task.CompletedTask;

        // Called on the main thread, where the raid generation is read; work gets it to compare against.
        private static Task<T> Enqueue<T>(Func<int, T> work)
        {
            int raidGen = RMSession.RaidGeneration;
            lock (QueueLock)
            {
                var next = _queueTail.ContinueWith(_ => raidGen == RMSession.RaidGeneration ? work(raidGen) : default, TaskScheduler.Default);
                _queueTail = next;
                return next;
            }
        }

        //====================[ Public API ]====================
        public static void NotifyBeginCritical(string playerId) => EnqueueReliable("begin-critical", playerId);

        // Rolls an authorized-but-abandoned revive start back to BleedingOut on the server.
        public static void NotifyReviveStartCancelled(string playerId) => EnqueueReliable("cancel-revive-start", playerId);

        public static void NotifyEndInvulnerability(string playerId) => EnqueueReliable("end-invulnerability", playerId);

        public static void NotifyReset(string playerId) => EnqueueReliable("reset", playerId);

        // Dropped at a raid boundary it comes back as default (not allowed); callers check the generation first.
        public static Task<AuthorizeResult> AuthorizeReviveStartAsync(string playerId, string reviverId, string source) =>
            Enqueue(_ =>
            {
                var response = Send("request-revive-start", new AuthorityRequest
                {
                    PlayerId = playerId,
                    ReviverId = reviverId,
                    Source = source
                });

                return response == null
                    ? new AuthorizeResult(false, PlayerFacingMessages.ReviveDenied.AuthorizationFailed)
                    : new AuthorizeResult(response.Success, MapDenyReason(response));
            });

        // Returns the post-revive lives count, or null if the server refused the completion or the raid ended first.
        public static Task<int?> NotifyReviveCompleteAsync(string playerId) =>
            Enqueue(raidGen =>
            {
                var response = SendReliable("complete-revive", new AuthorityRequest { PlayerId = playerId }, raidGen);
                if (response is { Success: true }) return response.State?.LivesRemaining;

                // A repeat of an attempt that reached the server but lost its answer (ours, or SPT's own
                // HTTP retry) finds the revive already completed. begin-critical for this downing sits
                // ahead of us in the queue, so a Revived state can only be this completion.
                if (response is { DenialCode: CompleteInvalidStateCode, State: { State: RevivedStateCode } })
                    return response.State.LivesRemaining;

                return (int?)null;
            });

        public static Task<int?> GetLivesAsync(string playerId) =>
            Enqueue(_ =>
            {
                var response = Send("get", new AuthorityRequest { PlayerId = playerId });
                return response is { Success: true } ? response.State?.LivesRemaining : (int?)null;
            });

        //====================[ Private Helpers ]====================
        private static void EnqueueReliable(string route, string playerId) =>
            Enqueue(raidGen => SendReliable(route, new AuthorityRequest { PlayerId = playerId }, raidGen));

        // Retries transport failures only (no answer); any server answer, accepted or denied, is final.
        // Each route retried here is safe to repeat back to back (SPT's HTTP client may already repeat a
        // POST on its own). Gives up once the raid ends, so a stale request never lands in the next raid.
        private static AuthorityResponse SendReliable(string route, AuthorityRequest data, int raidGen)
        {
            for (int attempt = 0; ; attempt++)
            {
                var response = Send(route, data);
                if (response != null) return response;

                int delay = attempt < RetryDelaysMs.Length ? RetryDelaysMs[attempt] : MaxRetryDelayMs;
                Plugin.LogSource.LogWarning($"[RevivalAuthority] {route} for {data.PlayerId} retrying in {delay / 1000}s (attempt {attempt + 2}).");
                Thread.Sleep(delay);

                if (raidGen != RMSession.RaidGeneration)
                {
                    Plugin.LogSource.LogWarning($"[RevivalAuthority] {route} for {data.PlayerId} abandoned: raid ended before the server answered.");
                    return null;
                }
            }
        }

        private static AuthorityResponse Send(string route, AuthorityRequest data)
        {
            try
            {
                return ModUtils.ServerRoute<AuthorityResponse>($"{BaseRoute}/{route}", data);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[RevivalAuthority] {route} failed: {ex.Message}");
                return null;
            }
        }

        private static string MapDenyReason(AuthorityResponse response)
        {
            if (response == null || response.Success)
            {
                return string.Empty;
            }

            switch (response.DenialCode)
            {
                case 1:
                    return PlayerFacingMessages.ReviveDenied.Cooldown;
                case 5:
                case 8:
                case 9:
                    return PlayerFacingMessages.ReviveDenied.AuthorizationFailed;
                case 7:
                    return PlayerFacingMessages.ReviveDenied.OutOfLives;
                default:
                    return string.IsNullOrWhiteSpace(response.Reason) ? string.Empty : response.Reason;
            }
        }
    }
}
