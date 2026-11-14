//====================[ Imports ]====================
using System;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RedeploySpawnPoints ]====================
    // Where a redeploying player comes back: the player-category spawn point nearest to their body, read from the
    // location the game loaded (the same list its own raid start picks from).
    internal static class RedeploySpawnPoints
    {
        //====================[ Types ]====================
        public readonly struct Spawn
        {
            public readonly Vector3 Position;
            public readonly float Yaw;

            public Spawn(Vector3 position, float yaw)
            {
                Position = position;
                Yaw = yaw;
            }
        }

        //====================[ Lookup ]====================
        // False when the game has no spawn list loaded or none of its points fits the player.
        public static bool TryFindNearest(Player player, Vector3 from, out Spawn spawn)
        {
            spawn = default;
            try
            {
                var game = Singleton<AbstractGame>.Instantiated
                    ? Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>
                    : null;
                var points = game?.Location?.SpawnPointParams;
                if (points == null || points.Length == 0) return false;

                EPlayerSideMask side = SideMask(player.Side);
                float bestSqr = float.MaxValue;
                bool found = false;

                foreach (var point in points)
                {
                    if ((point.Categories & ESpawnCategoryMask.Player) == 0) continue;
                    if ((point.Sides & side) == 0) continue;
                    if (point.Position == null) continue;

                    Vector3 position = point.Position;
                    float sqr = (position - from).sqrMagnitude;
                    if (sqr >= bestSqr) continue;

                    bestSqr = sqr;
                    spawn = new Spawn(position, point.Rotation);
                    found = true;
                }

                if (found)
                {
                    RevivalDebugLog.LogDebug(
                        $"[Redeploy] Nearest spawn for {player.ProfileId}: {spawn.Position} ({Mathf.Sqrt(bestSqr):F0} m away).");
                }
                return found;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Spawn lookup failed: {ex.Message}");
                return false;
            }
        }

        private static EPlayerSideMask SideMask(EPlayerSide side) => side switch
        {
            EPlayerSide.Usec => EPlayerSideMask.Usec,
            EPlayerSide.Bear => EPlayerSideMask.Bear,
            _ => EPlayerSideMask.Savage
        };
    }
}
