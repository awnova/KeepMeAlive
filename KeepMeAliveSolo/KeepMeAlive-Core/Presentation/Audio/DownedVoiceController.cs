//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EFT;
using EFT.CameraControl;
using EFT.HealthSystem;
using HarmonyLib;
using UnityEngine;

namespace KeepMeAlive.Presentation.Audio
{
    //====================[ DownedVoiceController ]====================
    // The voice of the local player bleeding out, from their own voice banks (vanilla or custom).
    // Schedules hit lines, breathing, agony, bleeding, and near-death lines until revival or death.
    internal static class DownedVoiceController
    {
        //====================[ Tuning ]====================
        // Seconds between lines. The bleed-out is minutes long; a line every few seconds sounds bad.
        private const float LineIntervalMin = 30f;
        private const float LineIntervalMax = 50f;
        // Each breath clip is a whole bout of laboured breathing (breath_death runs 15-24 s), so the breathing comes
        // in bouts with a quiet spell between them, not one loop after another as vanilla plays it.
        private const float BreathRestMin = 15f;
        private const float BreathRestMax = 30f;
        // After any other sound the next bout follows shortly, like gasping after crying out.
        private const float BreathAfterSoundMin = 2f;
        private const float BreathAfterSoundMax = 5f;
        // The near-death ("I'm out") lines are said at fixed points of the bleed-out, not at random: one less than its
        // whole minutes, evenly spaced, so at least a minute apart. 3:00 says them at 2:00 and 1:00 left, 4:00 at
        // 3:00, 2:00 and 1:00; under 2:00 none.
        private const float NearDeathMinSpacing = 60f;
        // Chance a line is a bleeding one; otherwise it is an agony line.
        private const float BleedChance = 0.1f;
        // Keep these lines below hit lines (OnBeingHurt, 2), so a new wound always cuts
        // into a line, as it cuts into breathing (0). The banks' own (agony 3, near-death/bleeding 2) would drop it.
        private const int LineImportance = 1;

        //====================[ Clip Filters ]====================
        // The 4th and 5th bleeding lines, the ones used. Voices name them usec4_bleed_in_04_n, bear1_bleed_in4_n /
        // bear1_bleed_in5_bl_n and Rob_bleeding_04_q, and the bank order does not follow the numbers. Voices
        // without them (W.A.A.C.'s have two or fewer bleeding lines) use any of theirs.
        private static readonly Regex BleedClips = new Regex(@"bleed(_in|ing)_?0?[45](?!\d)", RegexOptions.IgnoreCase);
        // bear_2's agony bank also holds a death scream (bear2_death8); a downed body must not sound dead.
        private static readonly Regex DeathClips = new Regex("death", RegexOptions.IgnoreCase);

        // Breath clips by their tags, best first: every vanilla voice tags one breath Dying (breath_death or
        // breath_neardeath), the one EFT loops for a player this hurt. 0 takes any breath clip.
        private static readonly int[] BreathTags = { (int)ETagStatus.Dying, (int)ETagStatus.BadlyInjured, 0 };

        //====================[ State ]====================
        private sealed class Voice
        {
            // Bleeding out and alive: the sounds are ours.
            public bool Owned;
            // How many of the near-death marks have been said in this downed state.
            public int NearDeathSaid;
            // Wait for the current sound to end before starting another.
            public float BusyUntil;
            public float NextLineTime;
            public float NextBreathTime;
            // The clip last seen on the speaker, so one the game started (a hit line) is noticed once.
            public TaggedClip LastClip;
        }

        private static readonly Dictionary<string, Voice> Voices = new Dictionary<string, Voice>();

        //====================[ Game Internals ]====================
        // BaseSpeaker._importance: what BaseSpeaker.Play compares a new phrase against while the speaker is busy.
        // PlayDirect keeps the last value, so set it for each sound.
        private static readonly AccessTools.FieldRef<BaseSpeaker, int> Importance =
            AccessTools.FieldRefAccess<BaseSpeaker, int>("_importance");
        // Player._speechSource: the speech source if one is up (the SpeechSource getter would create one).
        private static readonly AccessTools.FieldRef<Player, BetterSource> SpeechSource =
            AccessTools.FieldRefAccess<Player, BetterSource>("_speechSource");

        // BetterSource.PreOcclusionVolume: the source's volume before occlusion (base volume x the clip's).
        private static readonly AccessTools.FieldRef<BetterSource, float> PreOcclusionVolume =
            AccessTools.FieldRefAccess<BetterSource, float>("PreOcclusionVolume");
        // The main mixer's exposed volume parameter, the one the tinnitus ring ducks (AudioMixerDataContainer).
        private const string MainMixerVolumeParam = "MainVolume";

        //====================[ Tick ]====================
        public static void Tick(Player player)
        {
            if (player.Speaker == null) return;

            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st))
            {
                Voices.Remove(player.ProfileId);
                return;
            }

            var voice = GetVoice(player.ProfileId);
            bool alive = PlayerUtils.IsAlive(player);

            if (!OwnsBreath(player, st))
            {
                // Out of the bleed-out alive (a revive starting, or one completing): the voice is the game's again.
                if (voice.Owned && alive && !st.KillOverride) HandBack(player);
                voice.Owned = false;
                if (!st.IsCritical) voice.NearDeathSaid = 0;
                return;
            }

            if (!voice.Owned) TakeOver(voice, st);

            if (player.IsYourPlayer) TickOwner(player, st, voice);
        }

        // The owner's timeline: one sound at a time, each holding the rest back until it ends.
        private static void TickOwner(Player player, RMPlayer st, Voice voice)
        {
            var speaker = player.Speaker;

            // A hit line queues a follow-up (OnBeingHurtDissapoinment, 30-70% of the time) to play once it ends;
            // a downed player does not grumble after every hit.
            if (speaker.QueuedEvent == EPhraseTrigger.OnBeingHurtDissapoinment)
                speaker.QueuedEvent = EPhraseTrigger.PhraseNone;

            // Treat the game's hit line from a new wound like one of our sounds.
            if (speaker.Busy && speaker.Clip != voice.LastClip) Sounded(voice, speaker);

            if (Time.time < voice.BusyUntil) return;

            // A near-death mark passed: its line goes first, at the first free moment.
            int due = NearDeathMarksPassed(st.CriticalTimer);
            if (voice.NearDeathSaid < due)
            {
                bool played = PlayOwn(player, voice, EPhraseTrigger.HurtNearDeath, LineImportance);
                // As for a line: a voice without the bank skips the mark. Marks passed together are said once.
                if (played || !speaker.Busy) voice.NearDeathSaid = due;
                if (played)
                {
                    voice.NextLineTime = Mathf.Max(voice.NextLineTime, Time.time + RandomLineInterval());
                    return;
                }
            }

            if (Time.time >= voice.NextLineTime)
            {
                bool played = PlayLine(player, voice);
                // Not played while the speaker is free means the voice has nothing to say here: the slot is
                // skipped, not retried every tick (which would replay whatever bank it does have back to back).
                if (played || !speaker.Busy) voice.NextLineTime = Time.time + RandomLineInterval();
                if (played) return;
            }

            if (Time.time >= voice.NextBreathTime) PlayBreath(player, voice);
        }

        private static void TakeOver(Voice voice, RMPlayer st)
        {
            voice.Owned = true;
            voice.NextBreathTime = Time.time;
            voice.NextLineTime = Time.time + RandomLineInterval();
        }

        private static Voice GetVoice(string profileId)
        {
            if (!Voices.TryGetValue(profileId, out var voice))
            {
                voice = new Voice();
                Voices[profileId] = voice;
            }
            return voice;
        }

        private static float RandomLineInterval() => UnityEngine.Random.Range(LineIntervalMin, LineIntervalMax);

        // How many near-death marks the bleed-out has passed, with this much of it left.
        private static int NearDeathMarksPassed(float timeLeft)
        {
            float total = Mathf.Max(1f, SyncedServerConfigStore.Config.Gameplay.Downed.CriticalStateSeconds);
            int slots = Mathf.FloorToInt(total / NearDeathMinSpacing);
            if (slots < 2) return 0;
            float step = total / slots;
            return Mathf.Clamp(Mathf.FloorToInt((total - timeLeft) / step), 0, slots - 1);
        }

        // Manage this player's voice while they are alive and bleeding out. Their vanilla breath loop is held off
        // meanwhile. A revive in progress is the game's.
        public static bool OwnsBreath(Player player, RMPlayer st) =>
            st.State == RMState.BleedingOut && PlayerUtils.IsAliveHuman(player);

        public static bool OwnsBreath(Player player) =>
            player != null && RMSession.TryGetPlayerState(player.ProfileId, out var st) && OwnsBreath(player, st);

        // Raid boundary.
        public static void ClearAll() => Voices.Clear();

        //====================[ Owner Events ]====================
        // The local player has just gone down (from inside the downing Kill): the first knock. The downing hit has
        // usually said its hit line already (ApplyDamage raises ApplyDamageEvent before it kills); that is the cry.
        // Otherwise (a bleed, no hit line) one is said here. Called before the downed contusion, which waits for the
        // cry (DownedHealthAndEffectsManager).
        public static void OnLocalDowned(Player player)
        {
            if (player?.Speaker == null) return;
            try
            {
                var voice = GetVoice(player.ProfileId);
                voice.NearDeathSaid = 0;
                TakeOver(voice, RMSession.GetPlayerState(player.ProfileId));
                Cry(player, voice);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] OnLocalDowned failed for {player.ProfileId}: {ex.Message}");
            }
        }

        // The stim's needle going into the body or coming out of it during a self-revive:
        // a knock like the others.
        public static void OnStimNeedle(Player player)
        {
            if (!PlayerUtils.IsLocalPlayer(player) || player.Speaker == null) return;
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)) return;
            if (!OwnsBreath(player, st) && !(st.State == RMState.Reviving && PlayerUtils.IsAlive(player))) return;
            try
            {
                var voice = GetVoice(player.ProfileId);
                // Hand the voice back now if Tick has not yet: its HandBack would cut the cry short.
                if (st.State == RMState.Reviving && voice.Owned)
                {
                    HandBack(player);
                    voice.Owned = false;
                }
                Cry(player, voice);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] OnStimNeedle failed for {player.ProfileId}: {ex.Message}");
            }
        }

        // A knock to the body: a hit line over whatever is playing, unless one already is (the game's, from the hit that did it).
        private static void Cry(Player player, Voice voice)
        {
            var speaker = player.Speaker;
            if (!IsPlaying(speaker, EPhraseTrigger.OnBeingHurt))
            {
                speaker.Shut();
                Play(player, EPhraseTrigger.OnBeingHurt, null, null, 0, importance: null);
            }
            if (speaker.Busy) Sounded(voice, speaker);
        }

        // The bleed-out ended alive: stop the current line and hand breathing back to the game.
        private static void HandBack(Player player)
        {
            try
            {
                var source = SpeechSource(player);
                if (source != null) source.Stop();
                player.Speaker.Shut();
                if (!player.IsYourPlayer) return;
                player.HeavyBreath = false;
                player.UpdateBreathStatus();
                if (RevivalDebugLog.IsDebugLogsEnabled)
                    RevivalDebugLog.LogDebug($"[DownedVoice] {player.ProfileId} voice back to the game (breathing: {player.Speaker.Busy})");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] HandBack failed for {player.ProfileId}: {ex.Message}");
            }
        }

        //====================[ Death ]====================
        // The local player is about to die from the downed state: say the death line now, while they are alive.
        // The vanilla pick: OnDeath for weapon damage, otherwise OnAgony. A bleed-out or give-up is always voiced;
        // another death keeps vanilla's silence for a last hit to the head.
        public static void SpeakDeathLine(Player player, EDamageType damageType, bool always)
        {
            var speaker = player?.Speaker;
            if (speaker == null) return;
            try
            {
                if (!always && player.LastDamagedBodyPart == EBodyPart.Head) return;
                speaker.Shut();
                var trigger = damageType.IsWeaponInduced() ? EPhraseTrigger.OnDeath : EPhraseTrigger.OnAgony;
                Play(player, trigger, null, null, 0, importance: null);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] SpeakDeathLine failed for {player.ProfileId}: {ex.Message}");
            }
        }

        // Player.OnDead asks ShouldVocalizeDeath, then plays its own death line or, if not, Shut()s the speaker, which
        // releases the speech source and cuts whatever is playing. A downed player's death is voiced by what was said
        // before dying. So: with a sound playing, "vocalize" and the speaker is marked busy with nothing more important,
        // so OnDead's own pick is skipped by BaseSpeaker.Play's busy rule and the line plays out; with none, stay silent.
        public static bool DeathVoicedByOwner(Player player, out bool vocalize)
        {
            vocalize = false;
            var speaker = player?.Speaker;
            if (speaker == null || !RMSession.TryGetPlayerState(player.ProfileId, out var st) || !st.KillOverride) return false;
            if (speaker.Busy)
            {
                Importance(speaker) = int.MaxValue;
                vocalize = true;
            }
            return true;
        }

        //====================[ Lines And Breath ]====================
        // The odd bleeding line, otherwise an agony line. The near-death lines are on their marks (TickOwner).
        private static bool PlayLine(Player player, Voice voice)
        {
            if (UnityEngine.Random.value < BleedChance
                && (PlayOwn(player, voice, EPhraseTrigger.Bleeding, LineImportance, include: BleedClips)
                    || PlayOwn(player, voice, EPhraseTrigger.Bleeding, LineImportance)))
                return true;
            return PlayOwn(player, voice, EPhraseTrigger.OnAgony, LineImportance, exclude: DeathClips);
        }

        // One bout of breathing, then a quiet spell.
        private static void PlayBreath(Player player, Voice voice)
        {
            foreach (int tag in BreathTags)
            {
                if (!PlayOwn(player, voice, EPhraseTrigger.OnBreath, null, tag: tag)) continue;
                voice.NextBreathTime = voice.BusyUntil + UnityEngine.Random.Range(BreathRestMin, BreathRestMax);
                return;
            }
        }

        //====================[ Playback ]====================
        // Wait for any sound on the speaker to finish before starting another, and
        // a breath bout follows it shortly.
        private static void Sounded(Voice voice, BaseSpeaker speaker)
        {
            voice.LastClip = speaker.Clip;
            voice.BusyUntil = Time.time + Mathf.Max(0f, speaker.TimeLeft);
            voice.NextBreathTime = voice.BusyUntil + UnityEngine.Random.Range(BreathAfterSoundMin, BreathAfterSoundMax);
        }

        // Plays one of the downed player's own lines or breaths and puts it on the timeline. False if the voice has no
        // such bank, no clip qualifies, or the speaker is busy with something at least as important
        // (BaseSpeaker.Play's rule): a hit line is not cut off.
        private static bool PlayOwn(Player player, Voice voice, EPhraseTrigger trigger, int? importance,
            Regex include = null, Regex exclude = null, int tag = 0)
        {
            var speaker = player.Speaker;
            if (!CanStart(speaker, trigger)) return false;
            if (!Play(player, trigger, include, exclude, tag, importance)) return false;
            Sounded(voice, speaker);
            return true;
        }

        // The voice has a bank for the trigger and the speaker is free or busy with something less important.
        private static bool CanStart(BaseSpeaker speaker, EPhraseTrigger trigger) =>
            speaker.PhrasesBanks.TryGetValue(trigger, out var bank) && bank != null
            && !(speaker.Busy && bank.Importance <= speaker.Importance);

        // Whether the speaker is busy with a clip of the trigger's bank.
        private static bool IsPlaying(BaseSpeaker speaker, EPhraseTrigger trigger) =>
            speaker.Busy && speaker.Clip != null && speaker.PhrasesBanks.TryGetValue(trigger, out var bank) && bank != null
            && Array.IndexOf(bank.Clips, speaker.Clip) >= 0;

        // Starts a random clip of the player's bank for the trigger, optionally only among the clips whose names
        // match include and do not match exclude, and that carry the tag, and leaves the speaker's importance at the
        // given one (the bank's when null). False if the voice has no such bank or no clip qualifies.
        private static bool Play(Player player, EPhraseTrigger trigger, Regex include, Regex exclude, int tag, int? importance)
        {
            var speaker = player.Speaker;
            if (!speaker.PhrasesBanks.TryGetValue(trigger, out var bank) || bank == null) return false;

            int found = 0;
            int pick = -1;
            for (int i = 0; i < bank.Clips.Length; i++)
            {
                var tagged = bank.Clips[i];
                if (tagged == null) continue;
                if (tag != 0 && (tagged.Mask & tag) == 0) continue;
                if (include != null || exclude != null)
                {
                    var clip = tagged.Clip;
                    if (clip == null) continue;
                    if (include != null && !include.IsMatch(clip.name)) continue;
                    if (exclude != null && exclude.IsMatch(clip.name)) continue;
                }
                // Reservoir pick: each qualifying clip is equally likely without collecting them.
                if (UnityEngine.Random.Range(0, ++found) == 0) pick = i;
            }
            if (pick < 0) return false;

            try
            {
                speaker.PlayDirect(trigger, pick);
                Importance(speaker) = importance ?? bank.Importance;
                if (RevivalDebugLog.IsDebugLogsEnabled)
                    RevivalDebugLog.LogDebug($"[DownedVoice] {player.ProfileId} {trigger} #{pick}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] {trigger} #{pick} failed for {player.ProfileId}: {ex.Message}");
                return false;
            }
        }

        //====================[ Diagnostics ]====================
        // Debug log of what a downed player's phrase sounds like by the time it is playing.
        public static void TraceSpeech(Player player, TaggedClip clip)
        {
            if (!RevivalDebugLog.IsDebugLogsEnabled || player == null || clip == null) return;
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st) || !st.IsCritical) return;
            Plugin.StaticCoroutineRunner.StartCoroutine(TraceAfter(player, clip));
        }

        private static IEnumerator TraceAfter(Player player, TaggedClip clip)
        {
            yield return new WaitForSeconds(0.3f);
            if (player == null) yield break;
            try
            {
                var source = SpeechSource(player);
                string clipName = clip.Clip != null ? clip.Clip.name : "?";
                if (source == null)
                {
                    RevivalDebugLog.LogDebug($"[DownedVoice:trace] {player.ProfileId} {clipName}: no speech source 0.3 s after it started");
                    yield break;
                }

                var audio = source.source1;
                float duck = float.NaN;
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                    MonoBehaviourSingleton<BetterAudio>.Instance.Master.GetFloat(MainMixerVolumeParam, out duck);
                float distance = CameraManager.Exist ? CameraManager.Instance.Distance(source.Position) : float.NaN;

                RevivalDebugLog.LogDebug(
                    $"[DownedVoice:trace] {player.ProfileId} {clipName}: clip vol {clip.Volume:F2} falloff {clip.Falloff} | " +
                    $"source vol {audio.volume:F3} (pre-occlusion {PreOcclusionVolume(source):F3} x occlusion {source.OcclusionVolumeFactor:F2}) " +
                    $"playing {audio.isPlaying} mute {audio.mute} blend {audio.spatialBlend:F1} maxDist {audio.maxDistance:F0} dist {distance:F1} | " +
                    $"mixer '{(audio.outputAudioMixerGroup != null ? audio.outputAudioMixerGroup.name : "none")}' main {duck:F1} dB");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice:trace] failed: {ex.Message}");
            }
        }
    }
}
