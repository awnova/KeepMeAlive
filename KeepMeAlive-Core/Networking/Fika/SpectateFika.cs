//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using FikaCoopHandler = global::Fika.Core.Main.Components.CoopHandler;
using FikaFreeCamera = global::Fika.Core.Main.FreeCamera.FreeCamera;
using FikaPlayer = global::Fika.Core.Main.Players.FikaPlayer;

namespace KeepMeAlive.Networking.Fika
{
    //====================[ SpectateFika ]====================
    // Every Fika type the downed spectate cam touches lives here. If Fika changes CoopHandler, FikaPlayer or its
    // FreeCamera, this is the only file to fix. Reads only: nothing here calls into Fika's camera.
    internal static class SpectateFika
    {
        private static readonly Comparison<Player> ByNetId = (a, b) => ((FikaPlayer)a).NetId.CompareTo(((FikaPlayer)b).NetId);

        // Living, non-extracted human teammates other than the local player, in a stable (NetId) order.
        public static void GetTargets(List<Player> into)
        {
            into.Clear();
            if (!FikaCoopHandler.TryGetCoopHandler(out var handler) || handler.HumanPlayers == null) return;

            var extracted = handler.ExtractedPlayers;
            var humans = handler.HumanPlayers;
            for (int i = 0; i < humans.Count; i++)
            {
                var p = humans[i];
                if (p == null || p.IsYourPlayer || p.PlayerBones == null) continue;
                if (p.HealthController == null || !p.HealthController.IsAlive) continue;
                if (extracted != null && extracted.Contains(p.NetId)) continue;
                into.Add(p);
            }
            into.Sort(ByNetId);
        }

        // The transform the pose is built on: the 3rd person anchor Fika uses (LootRaycastOrigin), or the head bone.
        public static Transform Anchor(Player target, bool headcam)
        {
            var bones = target?.PlayerBones;
            if (bones == null) return null;
            return headcam ? bones.Head?.Original : bones.LootRaycastOrigin;
        }

        // I flip the shoulder with Fika's target left lean/stance.
        public static bool IsLeftStance(Player target) => target?.MovementContext != null && target.MovementContext.LeftStanceEnabled;

        // True while Fika's free/spectate camera owns the view. I leave it alone when extracted or dead.
        public static bool FikaCamActive()
        {
            if (!CameraManager.Exist) return false;
            var cam = CameraManager.Instance.Camera;
            if (cam == null) return false;
            var fika = cam.GetComponent<FikaFreeCamera>();
            return fika != null && fika.IsActive;
        }

        public static string Name(Player target) => target?.Profile?.Nickname ?? "?";
    }
}
