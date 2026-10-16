//====================[ Imports ]====================
using System;
using EFT;

namespace KeepMeAlive.Features
{
    //====================[ BodyAnimation ]====================
    // The one place that reaches every system posing players' bones itself, on top of or instead of EFT's animation:
    //   downed body     - BodyRagdoll (DownedPose's clips, the drag ragdoll, the blend back to EFT), per player tick;
    //   dragger's arms  - DraggerArms, after the dragger's own late pass;
    //   defib hands     - DefibHands, after the reviver's own late pass (the arms by ArmPoser, then the fingers and the paddles).
    internal static class BodyAnimation
    {
        // Script order of the components that write a body's bones in LateUpdate (DownedPose, BodyRagdoll's blend
        // out): after the player's own late pass and anything else of the game's, so theirs is the pose drawn.
        public const int LateWriteOrder = 32000;

        //====================[ Per Frame ]====================
        // Straight after each player's own late pass (PlayerPosePatch), so the game's pose is final and nothing of the
        // game's rewrites these bones afterwards.
        public static void AfterPlayerPose(Player player)
        {
            try { DraggerArms.AfterPlayerPose(player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DraggerArms] Pose error: {ex}"); }

            try { DefibHands.AfterPlayerPose(player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DefibHands] Pose error: {ex}"); }
        }

        //====================[ Raid Boundary ]====================
        // Forgets every body, drag and hands sequence; the players go with the scene.
        public static void ClearAll()
        {
            DragContext.ClearAll();
            BodyRagdoll.ClearAll();
            DefibHands.Clear();
        }
    }
}
