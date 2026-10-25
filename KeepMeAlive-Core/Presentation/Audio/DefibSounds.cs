using System;
using UnityEngine;

namespace KeepMeAlive.Presentation.Audio
{
    // The defibrillator's charge whine and shock, played in the world at the reviver's paddles (DefibHandsDriver times
    // them). Every peer plays the sequence, so every peer plays its own sounds: nothing is sent.
    internal static class DefibSounds
    {
        private const string ChargeResource = "KeepMeAlive.Resources.defib_charge.wav";
        private const string ShockResource = "KeepMeAlive.Resources.defib_shock.wav";
        // Audible range (m) and loudness. The game's own body sounds (fractures, headlamp clicks) use the Character group.
        private const int Rolloff = 30;
        private const float Volume = 1f;
        private const BetterAudio.AudioSourceGroupType Group = BetterAudio.AudioSourceGroupType.Character;

        private static AudioClip _charge, _shock;
        private static bool _loadAttempted;

        // The charge clip's length (s); 0 when it is missing.
        public static float ChargeSeconds => Charge != null ? Charge.length : 0f;

        private static AudioClip Charge { get { Load(); return _charge; } }
        private static AudioClip Shock { get { Load(); return _shock; } }

        // Reads both clips at raid start, so the first revive does not hitch.
        public static void Preload() => Load();

        public static BetterSource PlayCharge(Vector3 position) => Play(Charge, position);

        public static BetterSource PlayShock(Vector3 position) => Play(Shock, position);

        private static BetterSource Play(AudioClip clip, Vector3 position)
        {
            if (clip == null) return null;
            try
            {
                var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (betterAudio == null) return null;
                // OneShotPropagation: the occlusion the game gives its own one-shot world sounds (EffectsCommutator).
                return betterAudio.PlayAtPoint(position, clip, Group, Rolloff, Volume, EOcclusionTest.OneShotPropagation);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DefibSounds] Play error: {ex.Message}");
                return null;
            }
        }

        private static void Load()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            _charge = WavAudioLoader.LoadFromEmbeddedResource(ChargeResource, "defib_charge");
            _shock = WavAudioLoader.LoadFromEmbeddedResource(ShockResource, "defib_shock");
        }
    }
}
