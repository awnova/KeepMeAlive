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

            if (!GetTypeConfig(sourceKind).Enabled)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.FeatureDisabled, $"{sourceKind} revive is disabled by server config", entry);

            if (entry.CooldownUntilUnixSeconds - CooldownGraceSeconds > now)
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.Cooldown, "Player on cooldown", entry);

            if (entry.LivesRemaining < GetLivesCost(sourceKind))
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.OutOfLives, "Player is out of lives", entry);

            if (entry.State == RevivalState.Reviving)
            {
                bool stale = now - entry.LastUpdatedUnixSeconds > (long)ProgressSeconds + StaleRevivingGraceSeconds;
                // A self-revive may restart its own stuck attempt; nobody may take over a live team revive.
                bool selfRetry = sourceKind == RevivalSourceKind.Self && entry.Source == RevivalSourceKind.Self;
                if (!stale && !selfRetry)
                    return RevivalAuthorityResponse.Fail(RevivalDeniedCode.InvalidState, "A revive is already in progress", entry);

                logger.Info($"[KeepMeAlive.Server] Re-arming revive for {playerId} (stale={stale}, selfRetry={selfRetry}).");
            }
            else if (entry.State != RevivalState.BleedingOut)
            {
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.NotDowned, $"Player is not downed ({entry.State})", entry);
            }

            entry.State = RevivalState.Reviving;
            entry.Source = sourceKind;
            entry.ReviverId = sourceKind == RevivalSourceKind.Self ? playerId : reviverId;
            entry.LastUpdatedUnixSeconds = now;
            return RevivalAuthorityResponse.Ok(entry);
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
                return RevivalAuthorityResponse.Ok(entry);

            if (!callerIds.Contains(playerId) && !callerIds.Contains(entry.ReviverId))
                return RevivalAuthorityResponse.Fail(RevivalDeniedCode.NotAuthorized, "Only the active reviver can cancel this revive", entry);

            entry.State = RevivalState.BleedingOut;
            entry.Source = RevivalSourceKind.Self;
            entry.ReviverId = string.Empty;
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

            // Source/ReviverId were recorded by TryStartRevive and drive both the lives cost here
            // and the cooldown length in MarkCooldown.
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
            int maxLives = configService.Config.Gameplay.Downed.MaxLivesPerRaid;
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
                LivesRemaining = configService.Config.Gameplay.Downed.MaxLivesPerRaid
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

    // The client names the type; a missing name falls back to Self when the player revives themself, else Team.
    private static RevivalSourceKind ResolveSource(string playerId, string reviverId, string source)
    {
        if (string.IsNullOrEmpty(reviverId)
            || string.Equals(playerId, reviverId, StringComparison.Ordinal)
            || string.Equals(source, "self", StringComparison.OrdinalIgnoreCase))
        {
            return RevivalSourceKind.Self;
        }
        if (string.Equals(source, "defib", StringComparison.OrdinalIgnoreCase)) return RevivalSourceKind.Defib;
        return RevivalSourceKind.Team;
    }

    private ReviveTypeConfig GetTypeConfig(RevivalSourceKind source)
    {
        var gameplay = configService.Config.Gameplay;
        return source switch
        {
            RevivalSourceKind.Self => gameplay.Self,
            RevivalSourceKind.Defib => gameplay.Defib,
            _ => gameplay.Team
        };
    }

    private float GetCooldownSeconds(RevivalSourceKind source) => GetTypeConfig(source).CooldownSeconds;

    // Length of the revive progress phase after the hold, the same for every source (matches the client).
    private const float ProgressSeconds = 5f;

    private int GetLivesCost(RevivalSourceKind source) => GetTypeConfig(source).LivesCost;
}
