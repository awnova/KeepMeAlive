//====================[ Imports ]====================
using EFT;

namespace KeepMeAlive.Runtime.Lifecycle
{
    //====================[ PlayerHooks ]====================
    // EFT's own weapon-animation handlers on the local player that the downed code restores after stationary weapon detach.
    // Putting one back removes it first, so it can never end up subscribed twice.
    internal static class PlayerHooks
    {
        // The weapon animation's: the physical condition (stamina, overweight) it reads, and the raw pose it stores
        // for the arm IK every frame.
        public static void DetachWeaponAnimation(Player player)
        {
            if (player?.MovementContext == null || player.ProceduralWeaponAnimation == null) return;
            player.MovementContext.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
            player.ProceduralWeaponAnimation.OnPreCollision -= player.IkStoreRaw;
        }

        public static void AttachWeaponAnimation(Player player)
        {
            if (player?.MovementContext == null || player.ProceduralWeaponAnimation == null) return;
            DetachWeaponAnimation(player);
            player.MovementContext.PhysicalConditionChanged += player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
            player.ProceduralWeaponAnimation.OnPreCollision += player.IkStoreRaw;
        }
    }
}
