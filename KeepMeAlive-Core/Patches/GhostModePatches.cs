//====================[ Imports ]====================
using System;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using KeepMeAlive.Helpers;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ GhostModeGroupPatch ]====================
    internal class GhostModeGroupPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy), new[] { typeof(IPlayer), typeof(EBotEnemyCause) });

        [PatchPrefix]
        private static bool Prefix(IPlayer person, ref bool __result)
        {
            if (!GhostMode.ShouldIgnore(person)) return true;
            __result = false;
            return false;
        }
    }

    //====================[ GhostModeMemoryPatch ]====================
    internal class GhostModeMemoryPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMemory), nameof(BotMemory.AddEnemy), new[] { typeof(IPlayer), typeof(BotGroupEnemyInfo), typeof(bool) });

        [PatchPrefix]
        private static bool Prefix(IPlayer enemy) => !GhostMode.ShouldIgnore(enemy);
    }

    //====================[ GhostModeSAINPatch ]====================
    internal class GhostModeSAINPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SAIN")?
                .GetType("SAIN.SAINComponent.Classes.EnemyClasses.EnemyListController");

            return type != null ? AccessTools.Method(type, "tryAddEnemy", new[] { typeof(IPlayer) }) : null;
        }

        [PatchPrefix]
        private static bool Prefix(IPlayer enemyPlayer, ref object __result)
        {
            if (!GhostMode.ShouldIgnore(enemyPlayer)) return true;
            __result = null;
            return false;
        }
    }
}
