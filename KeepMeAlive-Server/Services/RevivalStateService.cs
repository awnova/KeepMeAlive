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
    // Extra time a Reviving entry may sit past its progress duration before it is treated as
    // abandoned (e.g. the reviver disconnected mid-revive).
    private const long StaleRevivingGraceSeconds = 15;

    //====================[ State ]====================
    private readonly Dictionary<string, RevivalStateEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    //====================[ Queries ]====================
    public RevivalStateEntry Get(string playerId)
    {
        lock (_sync) return GetOrCreateLocked(playerId) with { };
    }

    //====================[ Downed ]====================
    public RevivalStateEntry SetBleedingOut(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            entry.State = RevivalState.BleedingOut;
            entry.Source = RevivalSourceKind.Self;
            entry.ReviverId = string.Empty;
            entry.LastUpdatedUnixSeconds = Now();
            // CooldownUntilUnixSeconds is intentionally preserved so TryStartRevive can enforce it.
            return entry with { };
        }
    }

    //====================[ Authority Flow ]====================
    public RevivalAuthorityResponse TryStartRevive(string playerId, string reviverId, string source)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            var now = Now();
            var sourceKind = ResolveSource(playerId, reviverId, source);
            var mechanics = configService.Config.Gameplay.Revival;

            if (sourceKind == RevivalSourceKind.Self && !mechanics.EnableSelfRevive)
                return Denied(RevivalDeniedCode.FeatureDisabled, "Self revive is disabled by server config", entry);

            if (sourceKind == RevivalSourceKind.Team && !mechanics.EnableTeamRevive)
                return Denied(RevivalDeniedCode.FeatureDisabled, "Team revive is disabled by server config", entry);

            if (entry.CooldownUntilUnixSeconds - CooldownGraceSeconds > now)
                return Denied(RevivalDeniedCode.Cooldown, "Player on cooldown", entry);

            if (entry.LivesRemaining < GetLivesCost(sourceKind))
                return Denied(RevivalDeniedCode.OutOfLives, "Player is out of lives", entry);

            if (entry.State == RevivalState.Reviving)
            {
                bool stale = now - entry.LastUpdatedUnixSeconds > (long)GetProgressSeconds(entry.Source) + StaleRevivingGraceSeconds;
                // A self-revive may restart its own stuck attempt; nobody may take over a live team revive.
                bool selfRetry = sourceKind == RevivalSourceKind.Self && entry.Source == RevivalSourceKind.Self;
                if (!stale && !selfRetry)
                    return Denied(RevivalDeniedCode.InvalidState, "A revive is already in progress", entry);

                logger.Info($"[KeepMeAlive.Server] Re-arming revive for {playerId} (stale={stale}, selfRetry={selfRetry}).");
            }
            else if (entry.State != RevivalState.BleedingOut)
            {
                return Denied(RevivalDeniedCode.NotDowned, $"Player is not downed ({entry.State})", entry);
            }

            entry.State = RevivalState.Reviving;
            entry.Source = sourceKind;
            entry.ReviverId = sourceKind == RevivalSourceKind.Self ? playerId : reviverId;
            entry.LastUpdatedUnixSeconds = now;
            return Allowed(entry);
        }
    }

    // Rolls an authorized-but-abandoned revive start back to BleedingOut. Only the recorded
    // reviver (or the downed player) may do this.
    public RevivalAuthorityResponse CancelReviveStart(string playerId, ICollection<string> callerIds)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            if (entry.State != RevivalState.Reviving)
                return Allowed(entry);

            if (!callerIds.Contains(playerId) && !callerIds.Contains(entry.ReviverId))
                return Denied(RevivalDeniedCode.NotAuthorized, "Only the active reviver can cancel this revive", entry);

            entry.State = RevivalState.BleedingOut;
            entry.Source = RevivalSourceKind.Self;
            entry.ReviverId = string.Empty;
            entry.LastUpdatedUnixSeconds = Now();
            return Allowed(entry);
        }
    }

    public RevivalAuthorityResponse TryCompleteRevive(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            if (entry.State != RevivalState.Reviving)
                return Denied(RevivalDeniedCode.CompleteInvalidState, $"Cannot complete revive from {entry.State}", entry);

            // Source/ReviverId were recorded by TryStartRevive and drive both the lives cost here
            // and the cooldown length in MarkCooldown.
            entry.State = RevivalState.Revived;
            entry.LivesRemaining = Math.Max(0, entry.LivesRemaining - GetLivesCost(entry.Source));
            entry.LastUpdatedUnixSeconds = Now();
            return Allowed(entry);
        }
    }

    //====================[ Post-Revival State ]====================
    public RevivalStateEntry MarkCooldown(string playerId)
    {
        lock (_sync)
        {
            var entry = GetOrCreateLocked(playerId);
            entry.State = RevivalState.CoolDown;
            entry.LastUpdatedUnixSeconds = Now();
            entry.CooldownUntilUnixSeconds = entry.LastUpdatedUnixSeconds + (long)Math.Max(0, GetCooldownSeconds(entry.Source));
            entry.ReviverId = string.Empty;
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
            int maxLives = configService.Config.Gameplay.Revival.MaxLivesPerRaid;
            foreach (var id in playerIds)
            {
                var entry = GetOrCreateLocked(id);
                ResetLocked(entry);
                entry.LivesRemaining = maxLives;
            }
        }
    }

    //====================[ Helpers ]====================
    private RevivalStateEntry GetOrCreateLocked(string playerId)
    {
        if (!_entries.TryGetValue(playerId, out var entry))
        {
            entry = new RevivalStateEntry
            {
                PlayerId = playerId,
                LastUpdatedUnixSeconds = Now(),
                LivesRemaining = configService.Config.Gameplay.Revival.MaxLivesPerRaid
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
        entry.ReviverId = string.Empty;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static RevivalSourceKind ResolveSource(string playerId, string reviverId, string source)
    {
        if (string.Equals(source, "self", StringComparison.OrdinalIgnoreCase)
            || string.Equals(playerId, reviverId, StringComparison.Ordinal))
        {
            return RevivalSourceKind.Self;
        }

        return RevivalSourceKind.Team;
    }

    private float GetCooldownSeconds(RevivalSourceKind source)
    {
        var postRevive = configService.Config.Gameplay.PostRevive;
        return source == RevivalSourceKind.Self ? postRevive.Self.CooldownSeconds : postRevive.Team.CooldownSeconds;
    }

    private float GetProgressSeconds(RevivalSourceKind source)
    {
        var mechanics = configService.Config.Gameplay.Revival;
        return source == RevivalSourceKind.Self ? mechanics.SelfReviveProgressSeconds : mechanics.TeamReviveProgressSeconds;
    }

    private int GetLivesCost(RevivalSourceKind source)
    {
        var mechanics = configService.Config.Gameplay.Revival;
        return source == RevivalSourceKind.Self ? mechanics.SelfReviveLivesCost : mechanics.TeamReviveLivesCost;
    }

    private static RevivalAuthorityResponse Allowed(RevivalStateEntry state) =>
        new() { Success = true, State = state with { } };

    private static RevivalAuthorityResponse Denied(RevivalDeniedCode code, string reason, RevivalStateEntry state) =>
        new() { Success = false, DenialCode = code, Reason = reason, State = state with { } };
}
