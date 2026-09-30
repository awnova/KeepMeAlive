//====================[ Imports ]====================
using System;
using System.Threading.Tasks;

namespace KeepMeAlive.Helpers
{
    //====================[ RevivalAuthority ]====================
    // Client side of the server revive authority (/keepmealive/state/*).
    //
    // SPT's RequestHandler is synchronous, so every call runs off the Unity main thread. All calls
    // share ONE ordered background queue: a later request (e.g. request-revive-start) can never
    // overtake an earlier one (e.g. begin-critical) and be rejected for arriving first.
    // Results are handed back as Tasks; callers poll them from a coroutine and apply results on
    // the main thread.
    internal static class RevivalAuthority
    {
        //====================[ Constants ]====================
        private const string BaseRoute = "/keepmealive/state";

        //====================[ Network Models ]====================
        private sealed class AuthorityRequest
        {
            public string PlayerId { get; set; } = string.Empty;
            public string ReviverId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
        }

        private sealed class AuthorityStateDto
        {
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

        private static Task<T> Enqueue<T>(Func<T> work)
        {
            lock (QueueLock)
            {
                var next = _queueTail.ContinueWith(_ => work(), TaskScheduler.Default);
                _queueTail = next;
                return next;
            }
        }

        //====================[ Public API ]====================
        public static void NotifyBeginCritical(string playerId) =>
            Enqueue(() => Send("begin-critical", new AuthorityRequest { PlayerId = playerId }));

        // Rolls an authorized-but-abandoned revive start back to BleedingOut on the server.
        public static void NotifyReviveStartCancelled(string playerId) =>
            Enqueue(() => Send("cancel-revive-start", new AuthorityRequest { PlayerId = playerId }));

        public static void NotifyEndInvulnerability(string playerId) =>
            Enqueue(() => Send("end-invulnerability", new AuthorityRequest { PlayerId = playerId }));

        public static void NotifyReset(string playerId) =>
            Enqueue(() => Send("reset", new AuthorityRequest { PlayerId = playerId }));

        public static Task<AuthorizeResult> AuthorizeReviveStartAsync(string playerId, string reviverId, string source) =>
            Enqueue(() =>
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

        // Returns the post-revive lives count, or null if the server did not accept the completion.
        public static Task<int?> NotifyReviveCompleteAsync(string playerId) =>
            Enqueue(() =>
            {
                var response = Send("complete-revive", new AuthorityRequest { PlayerId = playerId });
                return response is { Success: true } ? response.State?.LivesRemaining : (int?)null;
            });

        public static Task<int?> GetLivesAsync(string playerId) =>
            Enqueue(() =>
            {
                var response = Send("get", new AuthorityRequest { PlayerId = playerId });
                return response is { Success: true } ? response.State?.LivesRemaining : (int?)null;
            });

        //====================[ Private Helpers ]====================
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
