using Comfort.Common;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Utilities
{
    internal static class PlayerUtils
    {
        public static Player GetYourPlayer()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            return (player != null && player.IsYourPlayer) ? player : null;
        }

        public static Player GetPlayerById(string id)
        {
            if (string.IsNullOrEmpty(id) || !Singleton<GameWorld>.Instantiated)
            {
                return null;
            }

            return Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(id);
        }

        public static string GetPlayerDisplayName(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                return playerId;
            }

            var p = GetPlayerById(playerId);
            var nick = p?.Profile?.Nickname;

            return string.IsNullOrEmpty(nick) ? playerId : NormalizeDisplayName(nick);
        }

        public static string GetPlayerDisplayName(Player player)
        {
            var nick = player?.Profile?.Nickname;
            if (!string.IsNullOrEmpty(nick))
            {
                return NormalizeDisplayName(nick);
            }

            return !string.IsNullOrEmpty(player?.ProfileId) ? player.ProfileId : "?";
        }

        // Tarkov nicknames arrive in whatever casing the player typed (all caps, all
        // lowercase, mixed) - normalize to Title Case so revive/heal messages read the
        // same regardless of how a given player set their own name.
        private static string NormalizeDisplayName(string name) =>
            char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();

        public static bool IsAlive(Player player) =>
            player?.HealthController is { IsAlive: true };

        public static bool IsHuman(Player player) =>
            player != null && !player.IsAI && player.AIData?.IsAI != true;

        public static bool IsAliveHuman(Player player) =>
            IsHuman(player) && IsAlive(player);

        public static bool IsLocalPlayer(Player player) =>
            player != null && player.IsYourPlayer;

        public static bool IsLocalHuman(Player player) =>
            IsLocalPlayer(player) && IsHuman(player);

        public static bool IsLocalPlayer(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                return false;
            }

            var local = GetYourPlayer();
            return local != null && local.ProfileId == profileId;
        }

        public static bool IsAliveAndStanding(Player player) =>
            IsAlive(player) && !RMSession.IsPlayerCritical(player.ProfileId);

        public static bool IsFirstPersonLocal(Player player) =>
            player != null && player.IsYourPlayer && player.FirstPersonPointOfView;

        public static bool TryGetLocalOwnerPlayer(GamePlayerOwner owner, out Player player)
        {
            player = owner?.Player;
            return player != null && player.IsYourPlayer;
        }

        // Nothing in the player's hands.
        public static bool IsEmptyHanded(Player player) =>
            player?.HandsController is Player.EmptyHandsController;

        // A firearm in the player's hands (it needs only the right; the left can keep a dragged body).
        public static bool HoldsFirearm(Player player) =>
            player?.HandsController is IFirearmHandsController;

        public static Transform GetPelvis(Player player) =>
            player?.PlayerBones?.Pelvis?.Original;

        public static Transform GetHead(Player player) =>
            player?.PlayerBones?.Head?.Original;

        public static Transform GetRibcage(Player player) =>
            player?.PlayerBones?.Ribcage?.Original;

        public static Transform GetSpine1(Player player) =>
            player?.PlayerBones?.Spine1;

        // The transform the spectate pose is built on: the 3rd person anchor Fika uses (LootRaycastOrigin), or the head bone.
        public static Transform GetSpectateAnchor(Player target, bool headcam)
        {
            var bones = target?.PlayerBones;
            if (bones == null) return null;
            return headcam ? bones.Head?.Original : bones.LootRaycastOrigin;
        }

        public static bool IsLeftStance(Player target) =>
            target?.MovementContext != null && target.MovementContext.LeftStanceEnabled;

        public static bool CanRotateProne(MovementContext mc, float deltaYaw, Transform pivot) =>
            mc != null && mc.IsAbleToRotateProne(Vector3.zero, deltaYaw, Quaternion.Euler(0f, 2f * deltaYaw, 0f), pivot, out _);

        public static void RotateBodyYaw(MovementContext mc, float deltaYaw)
        {
            if (mc == null) return;
            mc.ApplyRotation(Quaternion.AngleAxis(deltaYaw, Vector3.up) * mc.TransformRotation);
        }
    }
}
