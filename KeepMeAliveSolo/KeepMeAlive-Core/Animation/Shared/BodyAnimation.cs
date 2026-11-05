//====================[ Imports ]====================
namespace KeepMeAlive.Animation.Shared
{
    //====================[ BodyAnimation ]====================
    // The one place that reaches every system posing players' bones itself, on top of or instead of EFT's animation:
    //   downed body - BodyRagdoll (DownedPose's clips, the blend back to EFT), per player tick.
    internal static class BodyAnimation
    {
        // Script order of the components that write a body's bones in LateUpdate (DownedPose, BodyRagdoll's blend
        // out): after the player's own late pass and anything else of the game's, so theirs is the pose drawn.
        public const int LateWriteOrder = 32000;

        //====================[ Raid Boundary ]====================
        // Forgets every body; the players go with the scene.
        public static void ClearAll()
        {
            BodyRagdoll.ClearAll();
            FullScaleArms.ClearAll();
        }
    }
}
