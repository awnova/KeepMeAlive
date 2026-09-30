//====================[ Imports ]====================
using System;
using System.Collections;
using Comfort.Common;
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ HeartbeatEffect ]====================
    // Local-only audio cue that starts the instant the player goes critical and
    // slows down as bleedout approaches death, replacing the (now heavily
    // shortened) vanilla tinnitus ring as the primary "time remaining" signal.
    //
    // Plays the heartbeat clip once per beat at its natural pitch across the
    // full 160->30 BPM range. Each beat is timestamped so screen effects can lock to it.
    internal static class HeartbeatEffect
    {
        //====================[ Constants ]====================
        private const string ClipResourceName = "KeepMeAlive.Resources.heartbeat_loop.wav";
        private const string ClipAssetName = "heartbeat_loop";

        // Matches BetterAudio.StartTinnitusEffect's own hardcoded
        // Mathf.Max(15f, ...) floor (see BetterAudio.cs:654) - the tinnitus ring
        // is now capped to exactly this length, so phase 1 of the heartbeat ramp
        // is intentionally tied to the same window.
        private const float Phase1Duration = 15f;

        private const float Phase1StartBpm = 160f;
        private const float Phase1EndBpm = 80f;
        private const float Phase2EndBpm = 30f;

        //====================[ State ]====================
        private static AudioClip _clip;
        private static bool _loadAttempted;

        // Beat clock for visuals that lock to the audio (see DownedScreenEffects). Time.time matches
        // the coroutine's WaitForSeconds, so a beat's age is exact.
        private static float _lastBeatTime;
        private static float _lastBeatInterval;
        private static float _prevBeatInterval;

        private static bool _isRunning;

        // Age of the current beat in seconds, its interval, and the previous beat's interval.
        // False when no heartbeat is playing (clip missing, ended, or stopped).
        public static bool TryGetBeatAge(out float age, out float interval, out float prevInterval)
        {
            age = Time.time - _lastBeatTime;
            interval = _lastBeatInterval;
            prevInterval = _prevBeatInterval;
            return _isRunning && interval > 0f;
        }

        //====================[ Public API ]====================
        public static void Start(Player player, RMPlayer st)
        {
            try
            {
                if (player == null || st == null || !player.IsYourPlayer) return;
                if (st.HeartbeatCoroutine != null) return; // already running

                var clip = GetClip();
                if (clip == null) return;

                _isRunning = true;
                _lastBeatInterval = 0f;
                _prevBeatInterval = 0f;
                st.HeartbeatCoroutine = Plugin.StaticCoroutineRunner.StartCoroutine(RunHeartbeat(st, clip));
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[HeartbeatEffect] Start error: {ex.Message}");
            }
        }

        public static void Stop(RMPlayer st)
        {
            try
            {
                if (st?.HeartbeatCoroutine == null) return; // not this player's heartbeat (e.g. a remote player's death)
                _isRunning = false;
                Plugin.StaticCoroutineRunner.StopCoroutine(st.HeartbeatCoroutine);
                st.HeartbeatCoroutine = null;
                StopActiveBeat();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[HeartbeatEffect] Stop error: {ex.Message}");
            }
        }

        //====================[ Coroutine ]====================
        private static IEnumerator RunHeartbeat(RMPlayer st, AudioClip clip)
        {
            float critical = SyncedServerConfigStore.Config.Gameplay.Revival.CriticalStateSeconds;
            float phase1Duration = Mathf.Min(Phase1Duration, critical);
            float elapsed = 0f;

            while (elapsed < critical)
            {
                float bpm;
                if (elapsed < phase1Duration)
                {
                    float t = phase1Duration > 0f ? elapsed / phase1Duration : 1f;
                    bpm = Mathf.Lerp(Phase1StartBpm, Phase1EndBpm, t);
                }
                else
                {
                    float remaining = critical - phase1Duration;
                    float t2 = remaining > 0f ? (elapsed - phase1Duration) / remaining : 1f;
                    bpm = Mathf.Lerp(Phase1EndBpm, Phase2EndBpm, t2);
                }

                float interval = 60f / bpm;
                _lastBeatTime = Time.time;
                _prevBeatInterval = _lastBeatInterval;
                _lastBeatInterval = interval;
                PlayBeat(clip);

                yield return new WaitForSeconds(interval);
                elapsed += interval;
            }

            _isRunning = false;
            st.HeartbeatCoroutine = null;
            StopActiveBeat();
        }

        // Holds the currently-playing beat's source so each beat can be stopped directly.
        private static BetterSource _activeSource;

        private const BetterAudio.AudioSourceGroupType BeatGroup = BetterAudio.AudioSourceGroupType.Nonspatial;

        private static void PlayBeat(AudioClip clip)
        {
            try
            {
                var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (betterAudio == null) return;

                StopActiveBeat();

                var position = betterAudio.ListenerTransform != null ? betterAudio.ListenerTransform.position : Vector3.zero;
                _activeSource = betterAudio.PlayAtPoint(position, clip, 0f, BeatGroup, 50);
                _activeSource?.EnableStereo(true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[HeartbeatEffect] PlayBeat error: {ex.Message}");
            }
        }

        private static void StopActiveBeat()
        {
            if (_activeSource == null) return;
            try { _activeSource.Stop(0.02f); } // short fade so the cutoff itself doesn't click
            catch (Exception ex) { Plugin.LogSource.LogError($"[HeartbeatEffect] StopActiveBeat error: {ex.Message}"); }
            _activeSource = null;
        }

        //====================[ Clip Loading ]====================
        private static AudioClip GetClip()
        {
            if (_clip != null) return _clip;
            if (_loadAttempted) return null;

            _loadAttempted = true;
            _clip = WavAudioLoader.LoadFromEmbeddedResource(ClipResourceName, ClipAssetName);
            if (_clip == null)
                Plugin.LogSource.LogError("[HeartbeatEffect] Failed to load heartbeat clip; heartbeat effect disabled.");

            return _clip;
        }
    }
}
