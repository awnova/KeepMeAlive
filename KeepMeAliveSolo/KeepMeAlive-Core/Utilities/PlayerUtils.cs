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

        // A firearm in the player's hands.
        public static bool HoldsFirearm(Player player) =>
            player?.HandsController is IFirearmHandsController;

        // No hands change in flight: Process queue idle and no set-in-hands / remove-from-hands event open (IsChangingWeapon).
        public static bool IsHandsIdle(Player player) =>
            player != null
            && player.ProcessStatus == Player.EProcessStatus.None
            && !(player.InventoryController?.IsChangingWeapon ?? false);

        // A live controller and nothing in flight.
        public static bool HasUsableHands(Player player)
        {
            var hands = player?.HandsController;
            return hands != null && !hands.Destroyed && IsHandsIdle(player);
        }

        public static string DescribeHands(Player player)
        {
            if (player == null) return "player=null";
            var hands = player.HandsController;
            return $"hands={(hands != null ? hands.GetType().Name : "null")} destroyed={(hands != null && hands.Destroyed)} " +
                   $"process={player.ProcessStatus} scheduled={player.ScheduledProcess != null} " +
                   $"changingWeapon={player.InventoryController?.IsChangingWeapon ?? false}";
        }

        public static Transform GetPelvis(Player player) =>
            player?.PlayerBones?.Pelvis?.Original;

        public static Transform GetHead(Player player) =>
            player?.PlayerBones?.Head?.Original;
    }
}
