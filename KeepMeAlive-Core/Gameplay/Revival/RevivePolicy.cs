//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivePolicy ]====================
    internal static class RevivePolicy
    {
        private static SyncedRevivalMechanicsConfig Revival => SyncedServerConfigStore.Config.Gameplay.Revival;

        //====================[ Policy Queries ]====================
        public static bool IsEnabled(ReviveSource source)
        {
            return source switch
            {
                ReviveSource.Self => Revival.EnableSelfRevive,
                ReviveSource.Team => Revival.EnableTeamRevive,
                _ => true
            };
        }

        public static float GetHoldDuration(ReviveSource source)
        {
            float configured = source switch
            {
                ReviveSource.Self => Revival.SelfReviveHoldSeconds,
                ReviveSource.Team => Revival.TeamReviveHoldSeconds,
                _ => 2f
            };
            return Mathf.Max(0.1f, configured);
        }

        // Length of the revive progress phase after the hold, the same for every source.
        public const float ProgressSeconds = 5f;

        public static float GetProgressDuration(ReviveSource source) => ProgressSeconds;

        public static bool ShouldConsumeReviveItem(ReviveSource source)
        {
            return source switch
            {
                ReviveSource.Self => Revival.ConsumeReviveItemOnSelfRevive,
                ReviveSource.Team => Revival.ConsumeReviveItemOnTeamRevive,
                _ => false
            };
        }

        public static bool RequiresItem => !SyncedServerConfigStore.Config.Gameplay.Development.NoReviveItemRequired;

        public static bool MustConsumeItem(ReviveSource source) => RequiresItem && ShouldConsumeReviveItem(source);

        // Post-revive tuning (restore %, invuln, cooldown, debuffs) for the given revive source.
        public static SyncedPostReviveSourceConfig PostRevive(ReviveSource source)
        {
            var postRevive = SyncedServerConfigStore.Config.Gameplay.PostRevive;
            return source == ReviveSource.Self ? postRevive.Self : postRevive.Team;
        }
    }
}
