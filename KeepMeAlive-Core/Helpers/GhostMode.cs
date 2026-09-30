//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using KeepMeAlive.Fika;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ GhostMode ]====================
    // Removes downed players from AI enemy lists (Host-only for Fika compat) and handles SAIN reflection support.
    public static class GhostMode
    {
        //====================[ Fields & State ]====================
        private static readonly HashSet<string> _ghostedPlayers = new();
        // Groups that had the player as an enemy when ghosting began. On exit only these are
        // restored - re-adding to every group would turn neutral/friendly groups hostile.
        private static readonly Dictionary<string, List<BotsGroup>> _groupsToRestore = new();

        public static void ClearAll()
        {
            _ghostedPlayers.Clear();
            _groupsToRestore.Clear();
        }

        private static bool _sainReflectionInitialized;
        private static bool _sainAvailable;
        private static Type _sainBotComponentType;           // SAIN.Components.BotComponent
        private static PropertyInfo _sainEnemyControllerProp; // BotComponent.EnemyController
        private static MethodInfo _sainRemoveEnemyMethod;     // SAINEnemyController.RemoveEnemy(string)

        //====================[ Queries ]====================
        // Returns true if the given player profile is currently ghosted (invisible to AI).
        public static bool IsGhosted(string profileId) => _ghostedPlayers.Contains(profileId);

        // Shared query used by patches to check whether AI may target this player.
        public static bool ShouldIgnore(IPlayer player)
        {
            try
            {
                return player != null && IsGhosted(player.ProfileId);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] Error checking ghost state: {ex.Message}");
                return false;
            }
        }

        // Returns true if we are the host (have BotsController / BotOwner instances). Always true in single-player.
        private static bool IsHost => FikaBridge.IAmHost();

        //====================[ SAIN Reflection ]====================
        // Lazily initializes reflection handles for SAIN types/methods. Safe to call even if SAIN is not installed.
        private static void EnsureSAINReflection()
        {
            if (_sainReflectionInitialized) return;
            
            _sainReflectionInitialized = true;

            try
            {
                var sainAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SAIN");

                if (sainAssembly == null)
                {
                    Plugin.LogSource.LogInfo("[GhostMode] SAIN assembly not found — SAIN integration disabled");
                    _sainAvailable = false;
                    return;
                }

                // BotComponent : MonoBehaviour (on same GameObject as BotOwner)
                _sainBotComponentType = sainAssembly.GetType("SAIN.Components.BotComponent");
                if (_sainBotComponentType == null)
                {
                    Plugin.LogSource.LogWarning("[GhostMode] SAIN BotComponent type not found");
                    _sainAvailable = false;
                    return;
                }

                // BotComponent.EnemyController  (SAINEnemyController)
                _sainEnemyControllerProp = _sainBotComponentType.GetProperty(
                    "EnemyController",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_sainEnemyControllerProp == null)
                {
                    Plugin.LogSource.LogWarning("[GhostMode] SAIN EnemyController property not found");
                    _sainAvailable = false;
                    return;
                }

                // SAINEnemyController.RemoveEnemy(string profileId)
                var enemyControllerType = _sainEnemyControllerProp.PropertyType;
                _sainRemoveEnemyMethod = enemyControllerType.GetMethod(
                    "RemoveEnemy",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);

                if (_sainRemoveEnemyMethod == null)
                {
                    Plugin.LogSource.LogWarning("[GhostMode] SAIN RemoveEnemy(string) method not found");
                    _sainAvailable = false;
                    return;
                }

                _sainAvailable = true;
                Plugin.LogSource.LogInfo("[GhostMode] SAIN reflection initialized successfully");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[GhostMode] SAIN reflection init error: {ex.Message}");
                _sainAvailable = false;
            }
        }

        // Removes the player from every SAIN bot's enemy tracking via reflection.
        private static int RemoveFromSAIN(string profileId)
        {
            EnsureSAINReflection();
            if (!_sainAvailable) return 0;

            int removed = 0;
            try
            {
                foreach (var bo in FindAllBotOwners())
                {
                    // SAIN's BotComponent lives on the same GameObject as the BotOwner.
                    var sainBot = bo.GetComponent(_sainBotComponentType);
                    if (sainBot == null) continue;

                    try
                    {
                        var enemyController = _sainEnemyControllerProp.GetValue(sainBot);
                        if (enemyController == null) continue;

                        _sainRemoveEnemyMethod.Invoke(enemyController, new object[] { profileId });
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning($"[GhostMode] SAIN RemoveEnemy error for one bot: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] RemoveFromSAIN error: {ex.Message}");
            }
            return removed;
        }

        //====================[ Execution: Enter ]====================
        public static void EnterGhostMode(Player player)
        {
            if (player == null) return;
            string playerId = player.ProfileId;

            try
            {
                // Mark as ghosted FIRST so the AddEnemy patch blocks re-acquisition
                _ghostedPlayers.Add(playerId);

                if (!IsHost)
                {
                    Plugin.LogSource.LogInfo($"[GhostMode] Enter for {player.Profile.Nickname} ({playerId}) — CLIENT, flag set only");
                    return;
                }

                Plugin.LogSource.LogInfo($"[GhostMode] Enter for {player.Profile.Nickname} ({playerId}) — HOST");

                int removedBots = 0;
                int removedGroups = 0;
                int clearedGoals = 0;

                if (!_groupsToRestore.TryGetValue(playerId, out var restoreList))
                {
                    restoreList = new List<BotsGroup>();
                    _groupsToRestore[playerId] = restoreList;
                }

                // Remove from every BotsGroup FIRST
                foreach (var g in FindAllBotGroups())
                {
                    if (g == null || !g.Enemies.ContainsKey(player)) continue;

                    try
                    {
                        g.RemoveEnemy(player, EBotEnemyCause.initial);
                        if (!restoreList.Contains(g)) restoreList.Add(g);
                        removedGroups++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning($"[GhostMode] g.RemoveEnemy error: {ex.Message}");
                    }
                }

                // Remove the player from BotEnemiesController, clear GoalEnemy/LastEnemy, and stop shooting systems.
                foreach (var bo in FindAllBotOwners())
                {
                    try
                    {
                        var ec = bo?.EnemiesController;
                        if (ec != null && ec.EnemyInfos.ContainsKey(player))
                        {
                            ec.Remove(player);
                            removedBots++;
                        }

                        var mem = bo?.Memory;
                        if (mem == null) continue;

                        bool wasTargeting = false;

                        if (mem.GoalEnemy?.Person?.ProfileId == playerId)
                        {
                            mem.GoalEnemy = null;    // fires LoseTarget() + OnGoalEnemyChanged
                            clearedGoals++;
                            wasTargeting = true;
                        }
                        
                        if (mem.LastEnemy?.Person?.ProfileId == playerId)
                        {
                            mem.LastEnemy = null;    // prevents SuppressShoot fallback
                        }

                        if (wasTargeting)
                        {
                            try
                            {
                                bo.ShootData?.EndShoot();
                            }
                            catch (Exception ex)
                            {
                                Plugin.LogSource.LogWarning($"[GhostMode] EndShoot error: {ex.Message}");
                            }

                            try
                            {
                                bo.CalcGoal();
                            }
                            catch (Exception ex)
                            {
                                Plugin.LogSource.LogWarning($"[GhostMode] CalcGoal error: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning($"[GhostMode] bot cleanup error: {ex.Message}");
                    }
                }

                Plugin.LogSource.LogInfo($"[GhostMode] Enter done (HOST): removed from {removedBots} bot controllers, {removedGroups} groups, cleared {clearedGoals} GoalEnemy refs");

                // SAIN removal (runs AFTER vanilla so SAIN can clean up any remaining EnemyInfo refs)
                int sainRemoved = RemoveFromSAIN(playerId);
                if (sainRemoved > 0)
                {
                    Plugin.LogSource.LogInfo($"[GhostMode] SAIN: removed from {sainRemoved} SAIN bot enemy controllers");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] Enter error: {ex.Message}");
            }
        }

        public static void EnterGhostModeById(string playerId)
        {
            try
            {
                var p = ModUtils.GetPlayerById(playerId);
                if (p != null)
                {
                    EnterGhostMode(p);
                }
                else
                {
                    _ghostedPlayers.Add(playerId);
                    Plugin.LogSource.LogInfo($"[GhostMode] EnterById: player object not found for {playerId}, flag set only");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] EnterById({playerId}) error: {ex.Message}");
            }
        }

        //====================[ Execution: Exit ]====================
        public static void ExitGhostMode(Player player)
        {
            if (player == null) return;
            
            string playerId = player.ProfileId;

            if (!_ghostedPlayers.Remove(playerId))
            {
                return;
            }

            _groupsToRestore.TryGetValue(playerId, out var restoreList);
            _groupsToRestore.Remove(playerId);

            try
            {
                if (!IsHost)
                {
                    RevivalDebugLog.LogDebug($"[GhostMode] Exit for {playerId} - CLIENT, flag cleared only");
                    return;
                }

                int addedGroups = 0;
                if (restoreList == null) return;

                // GetPlayerById resolves via EverExisted, so this can be a player who already left the
                // world (extract/disconnect). Re-adding them hands bots an enemy that is still "alive" but
                // missing from GameWorld's alive map, which NREs in BotMemory.set_GoalEnemy.
                if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(playerId) == null)
                {
                    RevivalDebugLog.LogDebug($"[GhostMode] Exit for {playerId} (HOST): player no longer in world, skipping enemy restore");
                    return;
                }

                foreach (var g in restoreList)
                {
                    if (g == null || g.Enemies.ContainsKey(player)) continue;

                    try
                    {
                        if (g.AddEnemy(player, EBotEnemyCause.initial))
                        {
                            addedGroups++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning($"[GhostMode] g.AddEnemy error: {ex.Message}");
                    }
                }

                Plugin.LogSource.LogInfo($"[GhostMode] Exit for {playerId} (HOST): restored enemy status in {addedGroups} bot groups");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] Exit error: {ex.Message}");
            }
        }

        public static void ExitGhostModeById(string playerId)
        {
            try
            {
                var p = ModUtils.GetPlayerById(playerId);
                if (p != null)
                {
                    ExitGhostMode(p);
                }
                else
                {
                    ClearGhostFlag(playerId);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] ExitById({playerId}) error: {ex.Message}");
            }
        }

        //====================[ Cleanup ]====================
        // Clears the ghosted flag WITHOUT re-adding to enemy lists (used when player is truly dying).
        public static void ClearGhostFlag(string playerId)
        {
            _ghostedPlayers.Remove(playerId);
            _groupsToRestore.Remove(playerId);
        }

        //====================[ Bot Enumeration ]====================
        // Snapshot copy: removing enemies / recalculating goals can mutate the live bot set.
        private static List<BotOwner> FindAllBotOwners()
        {
            var list = new List<BotOwner>();
            try
            {
                var bots = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance.BotsController?.Bots : null;
                if (bots != null)
                {
                    foreach (var bo in bots.BotOwners)
                    {
                        if (bo != null) list.Add(bo);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] FindAllBotOwners error: {ex.Message}");
            }
            return list;
        }

        private static List<BotsGroup> FindAllBotGroups()
        {
            var set = new HashSet<BotsGroup>();
            foreach (var bo in FindAllBotOwners())
            {
                var g = bo.BotsGroup;
                if (g != null) set.Add(g);
            }
            return new List<BotsGroup>(set);
        }
    }
}
