//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivePolicy ]====================
    // One config section per revive type: Self, Team (a dragger's stim jab) and Defib (the standing defibrillator revive).
    internal static class RevivePolicy
    {
        //====================[ Policy Queries ]====================
        public static SyncedReviveTypeConfig For(ReviveSource source)
        {
            var gameplay = SyncedServerConfigStore.Config.Gameplay;
            return source switch
            {
                ReviveSource.Self => gameplay.Self,
                ReviveSource.Defib => gameplay.Defib,
                _ => gameplay.Team
            };
        }

        public static bool IsEnabled(ReviveSource source) => For(source).Enabled;

        public static bool IsSelfReviveEnabled => For(ReviveSource.Self).Enabled;

        // Whether a reviver carrying (or not carrying) the defib and stim this type needs may start it.
        public static bool HasRequiredItem(ReviveSource source, Player reviver) => MissingItem(source, reviver) == RequiredItem.None;

        // The first required item the reviver lacks, so the caller can say which.
        public static RequiredItem MissingItem(ReviveSource source, Player reviver)
        {
            if (RequireDefib(source) && !InventoryUtils.HasDefib(reviver)) return RequiredItem.Defib;
            if (RequireStim(source) && !InventoryUtils.HasStim(reviver)) return RequiredItem.Stim;
            return RequiredItem.None;
        }

        public enum RequiredItem { None, Defib, Stim }

        // Whether the reviver must carry a stim. Only the stim revives (Self and Team) use one.
        public static bool RequireStim(ReviveSource source) => source != ReviveSource.Defib && For(source).RequireStim;

        // Whether any stim revive needs the stim, e.g. to keep it from being looted off a downed player.
        public static bool IsStimRequiredAnywhere => RequireStim(ReviveSource.Self) || RequireStim(ReviveSource.Team);

        public static float GetHoldDuration(ReviveSource source) => Mathf.Max(0.1f, For(source).HoldSeconds);

        public static int LivesCost(ReviveSource source) => For(source).LivesCost;

        // Whether the reviver must carry a defib for this type. Only the Defib revive uses one; Self and Team use a stim.
        public static bool RequireDefib(ReviveSource source) => source == ReviveSource.Defib && For(source).RequireDefib;

        // Length of the revive progress phase after the hold, the same for every source.
        public const float ProgressSeconds = 5f;

        // Whether the revive deletes the stim it required. Only the stim revives (Self and Team) use one.
        public static bool ShouldConsumeStim(ReviveSource source) => RequireStim(source) && For(source).ConsumeStim;

        public static bool ShouldConsumeDefib(ReviveSource source) => source == ReviveSource.Defib && For(source).ConsumeDefib;

        // Post-revive tuning (restore %, invuln, cooldown, debuffs) for the given revive source.
        public static SyncedReviveTypeConfig PostRevive(ReviveSource source) => For(source);
    }
}
