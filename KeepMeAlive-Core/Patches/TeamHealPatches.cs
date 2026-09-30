//====================[ Imports ]====================
using System.Reflection;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using KeepMeAlive.Features;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ Team Heal Use-Time Patches ]====================
    // Applies the multiplier at the HealthEffectsComponent.UseTime getter. Med-use durations
    // read this property directly or through UseTimeFor(bodyPart), which builds on UseTime.
    internal class TeamHealDoMedEffectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect));

        [PatchPrefix]
        private static void Prefix(ActiveHealthController __instance, Item item)
        {
            if (__instance.Player != null && __instance.Player.IsYourPlayer) TeamHealUseTime.BeginMedEffect(item);
        }

        [PatchFinalizer]
        private static void Finalizer() => TeamHealUseTime.EndMedEffect();
    }

    internal class TeamHealUseTimePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(HealthEffectsComponent), nameof(HealthEffectsComponent.UseTime));

        [PatchPostfix]
        private static void Postfix(ref float __result) => __result = TeamHealUseTime.Scale(__result);
    }
}
