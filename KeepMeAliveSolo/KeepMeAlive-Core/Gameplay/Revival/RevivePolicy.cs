//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivePolicy ]====================
    // Revive policy queries for Self revive.
    internal static class RevivePolicy
    {
        //====================[ Policy Queries ]====================
        public static SyncedReviveTypeConfig For(ReviveSource _) => SyncedServerConfigStore.Config.Gameplay.Self;

        public static bool IsEnabled(ReviveSource source) => For(source).Enabled;

        public static bool IsSelfReviveEnabled => For(ReviveSource.Self).Enabled;

        // Whether a player carrying (or not carrying) the stim this type needs may start it.
        public static bool HasRequiredItem(ReviveSource source, Player player) => MissingItem(source, player) == RequiredItem.None;

        // The required item the player lacks, so the caller can say which.
        public static RequiredItem MissingItem(ReviveSource source, Player player)
        {
            if (RequireStim(source) && !InventoryUtils.HasStim(player)) return RequiredItem.Stim;
            return RequiredItem.None;
        }

        public enum RequiredItem { None, Stim }

        // Whether the player must carry a stim.
        public static bool RequireStim(ReviveSource source) => For(source).RequireStim;

        public static float GetHoldDuration(ReviveSource source) => Mathf.Max(0.1f, For(source).HoldSeconds);

        public static int LivesCost(ReviveSource source) => For(source).LivesCost;

        // MaxLivesPerRaid = 0 means unlimited lives. Unlimited is a huge count, so every lives check passes and the HUD
        // (which tests IsUnlimited) shows no number. Must match the server's RevivalStateService.StartingLives.
        public const int UnlimitedLives = int.MaxValue;

        public static bool IsUnlimited(int livesRemaining) => livesRemaining >= UnlimitedLives / 2;

        public static int StartingLives(int maxLivesPerRaid) => maxLivesPerRaid <= 0 ? UnlimitedLives : maxLivesPerRaid;

        // Enough lives for the cost, or AllowInsufficientLives with at least one left: the revive then spends the rest and
        // the next down is final. Must match the server's RevivalStateService.HasLivesFor.
        public static bool HasLivesFor(ReviveSource source, int livesRemaining)
        {
            var config = For(source);
            return livesRemaining >= config.LivesCost || (config.AllowInsufficientLives && livesRemaining > 0);
        }

        // The lives this revive actually takes, for the HUD: the cost, capped at what is left.
        public static int LivesSpent(ReviveSource source, int livesRemaining) => Mathf.Clamp(livesRemaining, 0, LivesCost(source));

        // Length of the revive progress phase after the hold.
        public const float ProgressSeconds = 5f;

        // Whether the revive deletes the stim it required.
        public static bool ShouldConsumeStim(ReviveSource source) => RequireStim(source) && For(source).ConsumeStim;

        // Post-revive tuning (restore %, invuln, cooldown, debuffs) for the given revive source.
        public static SyncedReviveTypeConfig PostRevive(ReviveSource source) => For(source);
    }
}
