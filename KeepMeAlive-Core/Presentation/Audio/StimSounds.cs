using System;
using UnityEngine;

namespace KeepMeAlive.Presentation.Audio
{
    // The stim pen's injection, played in the world at the hand holding the pen when its needle goes in: the dragger's jab
    // (DraggerArms) and the downed player's own (DownedPose). It starts on the same frame as the downed player's hit cry
    // (DownedVoiceController.OnStimNeedle), and the clip's click comes ~0.1 s in, so the click lands just after the cry
    // starts. Every peer plays the animation, so every peer plays its own sound: nothing is sent.
    internal static class StimSounds
    {
        private const string InjectResource = "KeepMeAlive.Resources.stim_inject.wav";
        // Audible range (m) and loudness, as the defibrillator's (DefibSounds).
        private const int Rolloff = 30;
        private const float Volume = 1f;
        private const BetterAudio.AudioSourceGroupType Group = BetterAudio.AudioSourceGroupType.Character;

        private static AudioClip _inject;
        private static bool _loadAttempted;

        // Reads the clip at raid start, so the first stim does not hitch.
        public static void Preload() => Load();

        public static void PlayInject(Vector3 position)
        {
            Load();
            if (_inject == null) return;
            try
            {
                var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (betterAudio == null) return;
                betterAudio.PlayAtPoint(position, _inject, Group, Rolloff, Volume, EOcclusionTest.OneShotPropagation);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[StimSounds] Play error: {ex.Message}");
            }
        }

        private static void Load()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            _inject = WavAudioLoader.LoadFromEmbeddedResource(InjectResource, "stim_inject");
        }
    }
}
