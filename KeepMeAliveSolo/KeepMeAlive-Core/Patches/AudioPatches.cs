//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KeepMeAlive.Patches
{
    //====================[ TinnitusCapPatch ]====================
    // BetterAudio.StartTinnitusEffect(time, clip) computes ring duration as
    // Mathf.Max(15f, time * 2f) - a hard 15s floor baked into the engine. The engine calls it when a
    // Contusion effect *starts* (~0.1s after DoContusion returns), not from DoContusion itself, so
    // KeepMeAlive's contusions are identified by the single-use token armed in TinnitusScope.Run.
    // Vanilla tinnitus from flashbangs, blunt headshots, stimulants, etc. must pass through untouched.
    // The ENABLE_TINNITUS_EFFECT toggle governs these KeepMeAlive contusion rings, not tinnitus in general.
    internal class TinnitusCapPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BetterAudio), nameof(BetterAudio.StartTinnitusEffect));

        [PatchPrefix]
        private static bool Prefix(ref float time)
        {
            if (!TinnitusScope.TryConsume()) return true;

            if (!KeepMeAliveSettings.ENABLE_TINNITUS_EFFECT.Value) return false;

            // Set our contusion-driven ring to the engine's shortest possible duration.
            time = 0f;
            return true;
        }
    }

    //====================[ TinnitusRing ]====================
    // Ends a running ring early. BetterAudio.float_2 is the ring's end time; its coroutine exits and
    // restores the mixer when Time.time >= float_2.
    internal static class TinnitusRing
    {
        private static readonly FieldInfo EndTime = AccessTools.Field(typeof(BetterAudio), "float_2");

        static TinnitusRing()
        {
            if (EndTime == null)
                Plugin.LogSource.LogWarning("[TinnitusRing] BetterAudio.float_2 not found; Stop() is a no-op.");
        }

        public static void Stop()
        {
            try
            {
                if (EndTime == null || !Singleton<BetterAudio>.Instantiated) return;
                EndTime.SetValue(Singleton<BetterAudio>.Instance, Time.time);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[TinnitusRing] Stop error: {ex.Message}");
            }
        }
    }

    //====================[ DownedBreathPatch ]====================
    // Player.UpdateBreathStatus starts the vanilla breath loop (OnBreath, replayed each time the speaker frees up).
    // While bleeding out the breathing is DownedVoiceController's, so the loop is held off; once a revive starts
    // it is the game's again.
    internal class DownedBreathPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.UpdateBreathStatus));

        [PatchPrefix]
        private static bool Prefix(Player __instance) => !DownedVoiceController.OwnsBreath(__instance);
    }

    //====================[ DownedVoiceTracePatch ]====================
    // Every phrase played on a player goes through PlaySpeechFromTime. For a downed
    // player the debug log gets what it ended up sounding like (DownedVoiceController.TraceSpeech).
    internal class DownedVoiceTracePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.PlaySpeechFromTime));

        [PatchPostfix]
        private static void Postfix(Player __instance, TaggedClip clip) =>
            DownedVoiceController.TraceSpeech(__instance, clip);
    }

    //====================[ DownedDeathLinePatch ]====================
    // Player.ShouldVocalizeDeath decides, in Player.OnDead, whether the body picks a death line of its own. A
    // downed player's death line is the owner's (DownedVoiceController.DeathVoicedByOwner).
    internal class DownedDeathLinePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.ShouldVocalizeDeath));

        [PatchPrefix]
        internal static bool Prefix(Player __instance, ref bool __result)
        {
            if (!DownedVoiceController.DeathVoicedByOwner(__instance, out bool vocalize)) return true;
            __result = vocalize;
            return false;
        }
    }
}
