//====================[ Imports ]====================
using EFT;

namespace KeepMeAlive.Runtime.Lifecycle
{
    //====================[ PlayerHooks ]====================
    // EFT's own handlers on a player that the downed code takes off and puts back. Putting one back removes it first,
    // so it can never end up subscribed twice.
    internal static class PlayerHooks
    {
        // The weapon animation's: the physical condition (stamina, overweight) it reads, and the raw pose it stores
        // for the arm IK every frame.
        public static void DetachWeaponAnimation(Player player)
        {
            player.MovementContext.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
            player.ProceduralWeaponAnimation.OnPreCollision -= player.IkStoreRaw;
        }

        public static void AttachWeaponAnimation(Player player)
        {
            DetachWeaponAnimation(player);
            player.MovementContext.PhysicalConditionChanged += player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
            player.ProceduralWeaponAnimation.OnPreCollision += player.IkStoreRaw;
        }

        // The movement-state handler (pedometer, state sounds). Only for an observed player: on the local player
        // Fika swaps EFT's for its own (SetupFikaAudio), so putting EFT's back would double every step.
        public static void DetachStateChanged(Player player) =>
            player.MovementContext.OnStateChanged -= player.MovementContextOnStateChanged;

        public static void AttachStateChanged(Player player)
        {
            DetachStateChanged(player);
            player.MovementContext.OnStateChanged += player.MovementContextOnStateChanged;
        }
    }
}
