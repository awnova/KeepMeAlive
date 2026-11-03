//====================[ Imports ]====================
using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ DefibDescriptionPatch ]====================
    // The defib's item description, in the player's own KeepMeAlive language. Every game text lookup ends in
    // TryGetLocalization, so answering the "<tpl> Description" key here covers inspect, trader and handbook alike.
    // Client-side because each client may run a different language; the server's locale data is shared by all.
    internal class DefibDescriptionPatch : ModulePatch
    {
        private const string DescriptionKey = DefibIds.TemplateId + " Description";

        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocalizationManager), nameof(LocalizationManager.TryGetLocalization));

        [PatchPrefix]
        private static bool Prefix(string id, ref string localizedValue, ref bool __result)
        {
            if (!string.Equals(id, DescriptionKey, StringComparison.Ordinal)) return true;

            localizedValue = Localization.Text("Item.DefibDescription");
            __result = true;
            return false;
        }
    }
}
