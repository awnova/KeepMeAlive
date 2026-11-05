//====================[ Imports ]====================
using System.Reflection;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ Team Heal Med-Effect Patches ]====================
    // DoMedEffect is where a team-heal item's use starts on the patient's machine (TeamHealPatient): the prefix marks the
    // call, the UseTime getter scales the use time inside it, the postfix answers the healer with the effect it made, and
    // the finalizer ends the mark whatever happened. Med-use durations read UseTime directly or through UseTimeFor(bodyPart),
    // which builds on it.
    internal class TeamHealDoMedEffectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect));

        [PatchPrefix]
        private static void Prefix(ActiveHealthController __instance, Item item) => TeamHealPatient.BeginMedEffect(__instance.Player, item);

        [PatchPostfix]
        private static void Postfix(IHealthEffect __result) => TeamHealPatient.OnMedEffect(__result);

        [PatchFinalizer]
        private static void Finalizer() => TeamHealPatient.EndMedEffect();
    }

    internal class TeamHealUseTimePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(HealthEffectsComponent), nameof(HealthEffectsComponent.UseTime));

        [PatchPostfix]
        private static void Postfix(ref float __result) => __result = TeamHealPatient.Scale(__result);
    }
}
