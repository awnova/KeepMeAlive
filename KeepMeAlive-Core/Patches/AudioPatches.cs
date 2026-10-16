//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using Fika.Core.Main.Players;
using HarmonyLib;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
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

    //====================[ LocalOnlyPhrasePatch ]====================
    // FikaPlayer.OnPhraseTold plays a phrase of the local player and sends it to every peer. The teammate-down
    // callout is only played here, as Player.OnPhraseTold does, and not sent: it is for this player's ears only,
    // like the toast. Every other phrase is sent as usual.
    internal class LocalOnlyPhrasePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FikaPlayer), nameof(FikaPlayer.OnPhraseTold));

        [PatchPrefix]
        private static bool Prefix(FikaPlayer __instance, TaggedClip clip)
        {
            if (!DownedVoiceController.PlayingLocalOnly) return true;
            __instance.PlayPhraseClip(clip);
            return false;
        }
    }

    //====================[ DownedVoiceTracePatch ]====================
    // Every phrase played on a player goes through PlaySpeechFromTime, on the owner and on every peer. For a downed
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

    //====================[ ObservedHitLinePatch ]====================
    // ObservedPlayer.ApplyHitDebuff is a shot landing on this peer's copy of another player (the shooter's and
    // everyone else's): it raises their awareness on enemy damage and plays a hit line (OnBeingHurt) right there. The
    // owner says their own hit line and Fika sends it, so while they bleed out the copy's is held off and everyone
    // hears the owner's. Only the hit line: the awareness is still raised, as Fika does.
    internal class ObservedHitLinePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.ApplyHitDebuff));

        [PatchPrefix]
        private static bool Prefix(ObservedPlayer __instance, EDamageType damageType)
        {
            if (!DownedVoiceController.VoicedByOwner(__instance)) return true;
            if (damageType.IsEnemyDamage()) __instance.IncreaseAwareness(20f);
            return false;
        }
    }

    //====================[ ObservedDamageHitLinePatch ]====================
    // Player.OnAudioHealthApplyDamage plays a hit line when the health controller reports damage; on a peer's copy
    // that is the owner's damage synced over (NetworkHealthController). Held off while they bleed out, as above.
    internal class ObservedDamageHitLinePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.OnAudioHealthApplyDamage));

        [PatchPrefix]
        private static bool Prefix(Player __instance) => !DownedVoiceController.VoicedByOwner(__instance);
    }

    //====================[ DownedDeathLinePatch ]====================
    // Player.ShouldVocalizeDeath decides, in Player.OnDead, whether the body picks a death line of its own. A
    // downed player's death line is the owner's, already playing on every peer (DownedVoiceController
    // .DeathVoicedByOwner). ObservedPlayer overrides it without calling base, so it is patched as well.
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

    internal class ObservedDownedDeathLinePatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.ShouldVocalizeDeath));

        [PatchPrefix]
        private static bool Prefix(ObservedPlayer __instance, ref bool __result) =>
            DownedDeathLinePatch.Prefix(__instance, ref __result);
    }
}
