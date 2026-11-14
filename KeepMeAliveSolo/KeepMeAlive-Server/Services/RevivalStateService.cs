//====================[ Imports ]====================
using KeepMeAlive.Server.Models.Revival;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace KeepMeAlive.Server.Services;

//====================[ RevivalStateService ]====================
// RAM-only revival authority. All mutation happens under _sync; every value handed out is a
// copy taken under the lock so callers (and the JSON serializer) never see a torn entry.
[Injectable(InjectionType.Singleton)]
public class RevivalStateService(ISptLogger<RevivalStateService> logger, RevivalConfigService configService)
{
    //====================[ Constants ]====================
    // Tolerance for client/server clock drift when a cooldown is about to expire.
    private const long CooldownGraceSeconds = 2;

    //====================[ State ]====================
    private readonly Dictionary<string, RevivalStateEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    //====================[ Downed ]====================
    public RevivalStateEntry SetBleedingOut(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            // A redeploy the client gave up waiting for (its answer never came) was charged here: hand the lives back.
            if (entry.State == RevivalState.Revived && entry.Source == RevivalSourceKind.Redeploy)
                entry.LivesRemaining = (int)Math.Min((long)int.MaxValue, (long)entry.LivesRemaining + entry.RedeployCharge);
            entry.RedeployCharge = 0;

            entry.State = RevivalState.BleedingOut;
            entry.Source = RevivalSourceKind.Self;
            entry.LastUpdatedUnixSeconds = Now();
            // CooldownUntilUnixSeconds is intentionally preserved so TryStartRevive can enforce it.
            return entry with { };
        }
    }

    //====================[ Authority Flow ]====================
    public RevivalAuthorityResponse TryStartRevive(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            var now = Now();
            const RevivalSourceKind sourceKind = RevivalSourceKind.Self;

            if (!GetTypeConfig(sourceKind).Enabled)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.FeatureDisabled, $"{sourceKind} revive is disabled by server config", entry);

            if (entry.CooldownUntilUnixSeconds - CooldownGraceSeconds > now)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.Cooldown, "Player on cooldown", entry);

            if (!HasLivesFor(sourceKind, entry.LivesRemaining))
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.OutOfLives, "Player is out of lives", entry);

            if (entry.State == RevivalState.Reviving)
            {
                logger.Info($"[KeepMeAlive.Server] Re-arming self-revive for {playerId}.");
            }
            else if (entry.State != RevivalState.BleedingOut)
            {
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.NotDowned, $"Player is not downed ({entry.State})", entry);
            }

            entry.State = RevivalState.Reviving;
            entry.Source = sourceKind;
            entry.LastUpdatedUnixSeconds = now;
            return RevivalAuthorityResponse.Ok(entry);
        }
    }

    // Redeploy has no progress phase: one call checks the rules, charges the cost and marks the player Revived, so the
    // client either gets the lives deducted and redeploys, or is denied and nothing changes.
    public RevivalAuthorityResponse TryRedeploy(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            var now = Now();
            const RevivalSourceKind sourceKind = RevivalSourceKind.Redeploy;

            if (!GetTypeConfig(sourceKind).Enabled)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.FeatureDisabled, "Redeploy is disabled by server config", entry);

            if (entry.CooldownUntilUnixSeconds - CooldownGraceSeconds > now)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.Cooldown, "Player on cooldown", entry);

            if (!HasLivesFor(sourceKind, entry.LivesRemaining))
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.OutOfLives, "Player is out of lives", entry);

            // A pending self-revive attempt of the same player is simply replaced.
            if (entry.State != RevivalState.BleedingOut && entry.State != RevivalState.Reviving)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.NotDowned, $"Player is not downed ({entry.State})", entry);

            entry.State = RevivalState.Revived;
            entry.Source = sourceKind;
            int livesBefore = entry.LivesRemaining;
            entry.LivesRemaining = Math.Max(0, livesBefore - GetLivesCost(sourceKind));
            entry.RedeployCharge = livesBefore - entry.LivesRemaining;
            entry.LastUpdatedUnixSeconds = now;
            return RevivalAuthorityResponse.Ok(entry);
        }
    }

    // Rolls an authorized-but-abandoned revive start back to BleedingOut.
    public RevivalAuthorityResponse CancelReviveStart(string playerId, ICollection<string> callerIds)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            if (entry.State != RevivalState.Reviving)
                return RevivalAuthorityResponse.Ok(entry);

            if (!callerIds.Contains(playerId))
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.NotAuthorized, "Only the downed player can cancel this revive", entry);

            entry.State = RevivalState.BleedingOut;
            entry.Source = RevivalSourceKind.Self;
            entry.LastUpdatedUnixSeconds = Now();
            return RevivalAuthorityResponse.Ok(entry);
        }
    }

    public RevivalAuthorityResponse TryCompleteRevive(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            if (entry.State != RevivalState.Reviving)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.CompleteInvalidState, $"Cannot complete revive from {entry.State}", entry);

            entry.State = RevivalState.Revived;
            entry.LivesRemaining = Math.Max(0, entry.LivesRemaining - GetLivesCost(entry.Source));
            entry.LastUpdatedUnixSeconds = Now();
            return RevivalAuthorityResponse.Ok(entry);
        }
    }

    //====================[ Post-Revival State ]====================
    public RevivalStateEntry MarkCooldown(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            if (entry.State is RevivalState.BleedingOut or RevivalState.Reviving)
                return entry with { };

            entry.State = RevivalState.CoolDown;
            entry.LastUpdatedUnixSeconds = Now();
            entry.CooldownUntilUnixSeconds = entry.LastUpdatedUnixSeconds + (long)Math.Max(0, GetCooldownSeconds(entry.Source));
            return entry with { };
        }
    }

    public RevivalStateEntry Reset(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            ResetLocked(entry);
            return entry with { };
        }
    }

    // Called from the raid lifecycle (start/end of a local raid) for every profile the session owns.
    public void ResetForRaid(IEnumerable<string> playerIds)
    {
        lock (_sync)
        {
            int maxLives = StartingLives();
            foreach (var id in playerIds)
            {
                var entry = GetOrCreateLocked(id);
                ResetLocked(entry);
                entry.LivesRemaining = maxLives;
            }
        }
    }

    //====================[ Helpers ]====================
    // MaxLivesPerRaid = 0 means unlimited. Must match the client's RevivePolicy.StartingLives.
    private const int UnlimitedLives = int.MaxValue;

    private int StartingLives()
    {
        int max = configService.Config.Gameplay.Downed.MaxLivesPerRaid;
        return max <= 0 ? UnlimitedLives : max;
    }

    private RevivalStateEntry GetOrCreateLocked(string playerId)
    {
        if (!_entries.TryGetValue(playerId, out var entry))
        {
            entry = new RevivalStateEntry
            {
                PlayerId = playerId,
                LastUpdatedUnixSeconds = Now(),
                LivesRemaining = StartingLives()
            };
            _entries[playerId] = entry;
        }

        return entry;
    }

    private static void ResetLocked(RevivalStateEntry entry)
    {
        entry.State = RevivalState.None;
        entry.Source = RevivalSourceKind.Self;
        entry.LastUpdatedUnixSeconds = Now();
        entry.CooldownUntilUnixSeconds = 0;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private ReviveTypeConfig GetTypeConfig(RevivalSourceKind source) => source == RevivalSourceKind.Redeploy
        ? configService.Config.Gameplay.Redeploy
        : configService.Config.Gameplay.Self;

    private float GetCooldownSeconds(RevivalSourceKind source) => GetTypeConfig(source).CooldownSeconds;

    private int GetLivesCost(RevivalSourceKind source) => GetTypeConfig(source).LivesCost;

    // Enough lives for the cost, or AllowInsufficientLives with at least one left; the completing call then spends the
    // rest (clamped at 0). Must match the client's RevivePolicy.HasLivesFor.
    private bool HasLivesFor(RevivalSourceKind source, int livesRemaining)
    {
        var config = GetTypeConfig(source);
        return livesRemaining >= config.LivesCost || (config.AllowInsufficientLives && livesRemaining > 0);
    }
}
