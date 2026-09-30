//====================[ Imports ]====================
using System.Reflection;
using HarmonyLib;
using KeepMeAlive.Features;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KeepMeAlive.Patches
{
    //====================[ Shared ]====================
    // BloodOnScreen only draws its red vignette after recent bleeding hits. While downed, these
    // patches draw the same vignette (material pass 1) directly, leaving EFT's blood-drop state
    // untouched, and desaturate the world image before any blood is drawn so blood stays red.
    internal static class DownedBloodVignette
    {
        private static readonly int DataId = Shader.PropertyToID("_Data");

        public static readonly AccessTools.FieldRef<BloodOnScreen, float> LastHitTime =
            AccessTools.FieldRefAccess<BloodOnScreen, float>("_lastHitTime");

        public static void Draw(BloodOnScreen self, RenderTexture source, RenderTexture destination)
        {
            var mat = self.VignetteMaterial;
            mat.SetVector(DataId, new Vector4(self.center.x, self.center.y, self.sharpness * 0.01f, DownedScreenEffects.DownedBloodDarkness * 0.02f));
            Graphics.Blit(source, destination, mat, 1);
        }
    }

    //====================[ DownedVignetteFlickerPatch ]====================
    // EFT scales the fast-vignette darkness by a looping flicker curve. While the downed effect is
    // active that factor is forced to 1 so the heartbeat dip is the only modulation.
    internal class DownedVignetteFlickerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EffectsController.CC_FastVignetteAccumulator), nameof(EffectsController.CC_FastVignetteAccumulator.UpdateAmount));

        [PatchPostfix]
        private static void Postfix(EffectsController.CC_FastVignetteAccumulator __instance)
        {
            if (!DownedScreenEffects.IsActive) return;
            DownedScreenEffects.ExpireIfStale();
            if (!DownedScreenEffects.OwnsVignette(__instance) || __instance.cc_FastVignette_0 == null) return;
            __instance.cc_FastVignette_0.darkness = Mathf.Clamp01(__instance.CurrentAmount()) * __instance.MaxEffectValue;
        }
    }

    //====================[ DownedBloodApplyVignettePatch ]====================
    // Blood drops are active: the original pipeline runs, but its vignette step ignores the
    // heavy-bleeding gate.
    internal class DownedBloodApplyVignettePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BloodOnScreen), nameof(BloodOnScreen.ApplyVignette));

        [PatchPrefix]
        private static bool Prefix(BloodOnScreen __instance, RenderTexture source, RenderTexture destination)
        {
            if (!DownedScreenEffects.DownedBloodActive) return true;
            DownedBloodVignette.Draw(__instance, source, destination);
            return false;
        }
    }

    //====================[ DownedBloodRenderPatch ]====================
    // Swaps the incoming world image for a desaturated copy (released in the postfix), so
    // everything BloodOnScreen draws afterwards keeps its colour. With no recent hit the original
    // would skip the vignette entirely, so it is drawn here instead.
    internal class DownedBloodRenderPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BloodOnScreen), nameof(BloodOnScreen.OnRenderImageInternal));

        [PatchPrefix]
        private static bool Prefix(BloodOnScreen __instance, ref RenderTexture source, RenderTexture destination, out RenderTexture __state)
        {
            __state = null;
            DownedScreenEffects.ExpireIfStale();
            if (!DownedScreenEffects.DownedBloodActive) return true;

            __state = DownedScreenEffects.GrayscaleCopy(source);
            if (__state != null) source = __state;

            if (Time.time - DownedBloodVignette.LastHitTime(__instance) <= __instance.MaxBloodTime) return true;

            DownedBloodVignette.Draw(__instance, source, destination);
            if (__state != null)
            {
                RenderTexture.ReleaseTemporary(__state);
                __state = null;
            }
            return false;
        }

        [PatchPostfix]
        private static void Postfix(RenderTexture __state)
        {
            if (__state != null) RenderTexture.ReleaseTemporary(__state);
        }
    }
}
