//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Audio.SpatialSystem;
using EFT;
using EFT.CameraControl;
using EFT.HealthSystem;
using Fika.Core.Main.Utils;
using HarmonyLib;
using UnityEngine;

namespace KeepMeAlive.Presentation.Audio
{
    //====================[ DownedVoiceController ]====================
    // The voice of a player bleeding out, from their own voice banks (vanilla or custom).
    //
    // One owner, one voice. Only the downed player's own client picks and starts a sound: it goes through
    // Speaker.PlayDirect, FikaPlayer.OnPhraseTold sends it (trigger + TaggedClip.NetId) and every peer plays that exact
    // clip on its copy, so everyone hears the same thing at the same time. While a player bleeds out, a peer's copy
    // says nothing of its own (DownedBreathPatch, ObservedHitLinePatch); outside the bleed-out voice is left to the
    // game and Fika.
    //
    // What the owner says while BleedingOut, on one timeline (Voice):
    //   - a hit line for every knock to the body: going down, being picked up, being dropped (Cry). When the hit that
    //     did it already said one, that is the cry. A new wound's hit line is the game's and always cuts in.
    //   - breathing, in bouts with a quiet spell between (the game's breath loop is held off).
    //   - an agony line every so often, the odd bleeding line and one near-death line, lying still or crawling.
    // I wait for any sound to end before playing another, then play a breath bout.
    // When a revive starts the voice goes back to the game: the player's health effects pick the breathing (HandBack).
    // A downed player's death line is the owner's too, said before the body dies (SpeakDeathLine).
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
        // Chance a line is the near-death one, until it has been said once in this downed state. A 3-minute
        // bleed-out has about four lines, so this makes the one near-death line likely without forcing it.
        private const float NearDeathChance = 0.4f;
        // Chance a line is a bleeding one; otherwise it is an agony line.
        private const float BleedChance = 0.1f;
        // I keep these lines below hit lines (OnBeingHurt, 2), so a new wound always cuts
        // into a line, as it cuts into breathing (0). The banks' own (agony 3, near-death/bleeding 2) would drop it.
        private const int LineImportance = 1;
        // Height above the player's feet (m) that their sound is occluded from while downed, about a crouched head.
        // The container sits on Spine1, a hand's width off the floor on a body lying down, where the obstruction
        // rays from the listener clip the floor, terrain and low props and the height-difference cut kicks in; the
        // voice would come out muffled and far too quiet. The sound still plays from the head where the body lies.
        private const float OcclusionHeight = 0.6f;

        //====================[ Clip Filters ]====================
        // The 4th and 5th bleeding lines, the ones used. Voices name them usec4_bleed_in_04_n, bear1_bleed_in4_n /
        // bear1_bleed_in5_bl_n and Rob_bleeding_04_q, and the bank order does not follow the numbers. Voices
        // without them (W.A.A.C.'s have two or fewer bleeding lines) use any of theirs.
        private static readonly Regex BleedClips = new Regex(@"bleed(_in|ing)_?0?[45](?!\d)", RegexOptions.IgnoreCase);
        // The friendly_hit lines of a voice's Hit bank (usec4_friendly_hit_01_n, bear1_friendly_hit1_l, ...).
        private static readonly Regex FriendlyClips = new Regex("friendly", RegexOptions.IgnoreCase);
        // usec_2's OnFriendlyDown bank also holds a combat line (usec2_fight65_bl_l).
        private static readonly Regex FightClips = new Regex("fight", RegexOptions.IgnoreCase);
        // bear_2's agony bank also holds a death scream (bear2_death8); a downed body must not sound dead.
        private static readonly Regex DeathClips = new Regex("death", RegexOptions.IgnoreCase);

        // Breath clips by their tags, best first: every vanilla voice tags one breath Dying (breath_death or
        // breath_neardeath), the one EFT loops for a player this hurt. 0 takes any breath clip.
        private static readonly int[] BreathTags = { (int)ETagStatus.Dying, (int)ETagStatus.BadlyInjured, 0 };

        //====================[ State ]====================
        // One per player, on every peer. The timeline fields are only used on the owner.
        private sealed class Voice
        {
            // Bleeding out and alive: the sounds are the owner's.
            public bool Owned;
            public bool NearDeathSaid;
            // A dragger had the body at the last tick. A change is a knock.
            public bool WasDragged;
            // I wait for the current sound to end before starting another.
            public float BusyUntil;
            public float NextLineTime;
            public float NextBreathTime;
            // The clip last seen on the speaker, so one the game started (a hit line) is noticed once.
            public TaggedClip LastClip;
            // I set the occlusion offset on the player's sound container.
            public bool Lifted;
        }

        private static readonly Dictionary<string, Voice> Voices = new Dictionary<string, Voice>();

        // True while a phrase that stays on this machine is being started on the local player (LocalOnlyPhrasePatch).
        public static bool PlayingLocalOnly { get; private set; }

        //====================[ Game Internals ]====================
        // BaseSpeaker._importance: what BaseSpeaker.Play compares a new phrase against while the speaker is busy.
        // PlayDirect keeps the last value, so I set it for each sound.
        private static readonly AccessTools.FieldRef<BaseSpeaker, int> Importance =
            AccessTools.FieldRefAccess<BaseSpeaker, int>("_importance");
        // Player._speechSource: the speech source if one is up (the SpeechSource getter would create one).
        private static readonly AccessTools.FieldRef<Player, BetterSource> SpeechSource =
            AccessTools.FieldRefAccess<Player, BetterSource>("_speechSource");

        // SpatialAudioSystem's per-player sound container (all of a player's sources, occluded together) and
        // occlusion processor, by IPlayer.Id.
        private static readonly AccessTools.FieldRef<SpatialAudioSystem, Dictionary<int, AudioSourceContainer>> PlayerContainers =
            AccessTools.FieldRefAccess<SpatialAudioSystem, Dictionary<int, AudioSourceContainer>>("_registeredPlayerContainers");
        private static readonly AccessTools.FieldRef<SpatialAudioSystem, Dictionary<int, SoundOcclusionProcessor>> PlayerProcessors =
            AccessTools.FieldRefAccess<SpatialAudioSystem, Dictionary<int, SoundOcclusionProcessor>>("_registeredPlayerProcessors");
        private static readonly AccessTools.FieldRef<SoundOcclusionProcessor, SpatialAudioOccluder> Occluder =
            AccessTools.FieldRefAccess<SoundOcclusionProcessor, SpatialAudioOccluder>("_occluder");
        private static readonly AccessTools.FieldRef<SpatialAudioOccluder, bool> OccluderDisposed =
            AccessTools.FieldRefAccess<SpatialAudioOccluder, bool>("_isDisposed");

        // BetterSource.PreOcclusionVolume: the source's volume before occlusion (base volume x the clip's).
        private static readonly AccessTools.FieldRef<BetterSource, float> PreOcclusionVolume =
            AccessTools.FieldRefAccess<BetterSource, float>("PreOcclusionVolume");
        // The main mixer's exposed volume parameter, the one the tinnitus ring ducks (AudioMixerDataContainer).
        private const string MainMixerVolumeParam = "MainVolume";

        //====================[ Tick ]====================
        // Per tick for every non-AI player, local or remote. Every peer tracks who is bleeding out; only the owner
        // makes sounds.
        public static void Tick(Player player)
        {
            if (player.Speaker == null) return;

            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st))
            {
                if (Voices.TryGetValue(player.ProfileId, out var gone))
                {
                    SetOcclusionLift(player, gone, false);
                    Voices.Remove(player.ProfileId);
                }
                return;
            }

            var voice = GetVoice(player.ProfileId);
            bool alive = player.HealthController is { IsAlive: true };

            // The body lies on the floor for the whole downed state, the revive included.
            SetOcclusionLift(player, voice, st.IsCritical && alive);

            if (!OwnsBreath(player, st))
            {
                // Out of the bleed-out alive (a revive starting, or one completing): the voice is the game's again.
                if (voice.Owned && alive && !st.KillOverride) HandBack(player);
                voice.Owned = false;
                if (!st.IsCritical) voice.NearDeathSaid = false;
                return;
            }

            // Down on this peer's view, or back in the bleed-out after a cancelled revive.
            if (!voice.Owned) TakeOver(voice, st);

            // A peer's copy plays what the owner sends.
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

            // I treat the game's hit line from a new wound like one of my sounds.
            if (speaker.Busy && speaker.Clip != voice.LastClip) Sounded(voice, speaker);

            // A dragger picked the body up or dropped it: a knock like the one that downed it. The drag claim is the
            // only sign of either here, since IsLimp stays set through the whole drag.
            if (st.IsBeingDragged != voice.WasDragged)
            {
                voice.WasDragged = st.IsBeingDragged;
                Cry(player, voice);
            }

            if (Time.time < voice.BusyUntil) return;

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
            voice.WasDragged = st.IsBeingDragged;
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

        // I manage this player's voice while they are alive and bleeding out. Their vanilla breath loop is held off
        // meanwhile. A revive in progress is the game's.
        public static bool OwnsBreath(Player player, RMPlayer st) =>
            st.State == RMState.BleedingOut && !player.IsAI && player.HealthController is { IsAlive: true };

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
                voice.NearDeathSaid = false;
                TakeOver(voice, RMSession.GetPlayerState(player.ProfileId));
                Cry(player, voice);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] OnLocalDowned failed for {player.ProfileId}: {ex.Message}");
            }
        }

        // A knock to the body (going down, being picked up, being dropped): a hit line over whatever is playing,
        // unless one already is (the game's, from the hit that did it).
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

        // The bleed-out ended alive, so I stop this line on every peer when a revive starts.
        // The owner picks a breathing phrase from health; I stop it on peers when its network phrase arrives.
        // The stop is immediate: a fading source stops whatever plays on it when the fade ends.
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
        // The local player is about to die from the downed state: say the death line now, while they are alive, so
        // Fika sends it and every peer hears this one; each peer's own (Player.OnDead) gives way to it (see
        // DeathVoicedByOwner). The vanilla pick: OnDeath for weapon damage, otherwise OnAgony. A bleed-out or
        // give-up is always voiced; another death keeps vanilla's silence for a last hit to the head
        // (Player.ShouldVocalizeDeath's rule, which is patched for this death, so not called here).
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
        // releases the speech source and cuts whatever is playing. A downed player's death (KillOverride on every
        // peer by then) is voiced by what the owner said before dying, sent to everyone. So: with a sound playing,
        // "vocalize" and the speaker is marked busy with nothing more important, so OnDead's own pick is skipped by
        // BaseSpeaker.Play's busy rule and the owner's line plays out; with none, stay silent. False when the death
        // is not a downed one: the game decides.
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

        //====================[ Observers ]====================
        // Whether this is a peer's copy of a player bleeding out, whose every sound comes from the owner. Its own hit
        // lines (ObservedHitLinePatch) are held off like its breath loop (DownedBreathPatch); they would come on top
        // of the owner's, with other clips at other times.
        public static bool VoicedByOwner(Player player) => player != null && !player.IsYourPlayer && OwnsBreath(player);

        //====================[ Teammate Down ]====================
        // A teammate went down: the toast, and the local player calling it out in their own voice. The callout is
        // for this player's ears only (not sent to the other peers), like the toast.
        public static void OnTeammateDowned(string profileId)
        {
            // A headless host has no screen or ears.
            if (FikaBackendUtils.IsHeadless) return;

            VFX_UI.Text(Color.red, PlayerFacingMessages.Downed.TeammateDown(ModUtils.GetPlayerDisplayName(profileId)));

            var local = ModUtils.GetYourPlayer();
            if (local == null || local.Speaker == null || local.HealthController is not { IsAlive: true }) return;
            // A downed player is busy with their own voice.
            if (RMSession.IsPlayerCritical(local.ProfileId)) return;

            // Every voice's friendly_hit lines are in its Hit bank (the squad's "teammate hit" call), except where
            // that bank holds something else (usec_2, usec_3): then its OnFriendlyDown "teammate down" lines.
            try
            {
                PlayingLocalOnly = true;
                if (!PlayIfFree(local, EPhraseTrigger.Hit, FriendlyClips, null))
                    PlayIfFree(local, EPhraseTrigger.OnFriendlyDown, null, FightClips);
            }
            finally
            {
                PlayingLocalOnly = false;
            }
        }

        //====================[ Lines And Breath ]====================
        // One near-death line per downed state, the odd bleeding line, otherwise an agony line.
        private static bool PlayLine(Player player, Voice voice)
        {
            if (!voice.NearDeathSaid && UnityEngine.Random.value < NearDeathChance
                && PlayOwn(player, voice, EPhraseTrigger.HurtNearDeath, LineImportance))
            {
                voice.NearDeathSaid = true;
                return true;
            }
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
        // I wait for any sound on the speaker to finish before starting another, and
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

        // The same, off the timeline, for a phrase that must not cut into the player's current one (the callout).
        private static bool PlayIfFree(Player player, EPhraseTrigger trigger, Regex include, Regex exclude) =>
            CanStart(player.Speaker, trigger) && Play(player, trigger, include, exclude, 0, importance: null);

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
        // given one (the bank's when null). On the owner Fika sends it to every peer. False if the voice has no such
        // bank or no clip qualifies.
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
                    RevivalDebugLog.LogDebug($"[DownedVoice] {player.ProfileId} {trigger} #{pick} ({DescribeOcclusion(player)})");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice] {trigger} #{pick} failed for {player.ProfileId}: {ex.Message}");
                return false;
            }
        }

        //====================[ Occlusion ]====================
        // Raises (or clears) the point the player's sounds are occluded from to OcclusionHeight above their feet.
        // AudioSourceContainer.CurrentPosition is its parent (Spine1) plus this offset; it feeds the obstruction
        // rays and the height-difference volume cut, not where the sources play. Only peers' copies have one: the
        // local player's own sounds are not occluded.
        private static void SetOcclusionLift(Player player, Voice voice, bool lift)
        {
            if (!lift && !voice.Lifted) return;
            var container = GetContainer(player);
            if (container == null)
            {
                voice.Lifted = false;
                return;
            }

            Vector3 offset = Vector3.zero;
            var spine = player.PlayerBones?.Spine1;
            if (lift && spine != null)
                offset.y = Mathf.Max(0f, player.Transform.position.y + OcclusionHeight - spine.position.y);
            container.SetPositionOffset(offset);
            voice.Lifted = lift;
        }

        private static AudioSourceContainer GetContainer(Player player)
        {
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return null;
            var system = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            return PlayerContainers(system).TryGetValue(player.Id, out var container) ? container : null;
        }

        //====================[ Diagnostics ]====================
        // The occluder's current effects on the player's sounds, for the debug log.
        private static string DescribeOcclusion(Player player)
        {
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return "no audio system";
            var system = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            if (!PlayerProcessors(system).TryGetValue(player.Id, out var processor) || processor == null) return "no processor";
            var occluder = Occluder(processor);
            if (occluder == null) return "no occluder";
            var container = GetContainer(player);
            string at = container != null ? $"{container.CurrentPosition.y - player.Transform.position.y:F2} m up" : "?";
            // The game disposes a player's occluder when their last speech source is released (the container is not
            // long-term), after which their sounds get no occlusion updates at all.
            return $"occluder {(OccluderDisposed(occluder) ? "DISPOSED" : "live")}, obstruction {occluder.ObstructionEffect:F2}, " +
                   $"propagation {occluder.PropagationEffect:F2}, occlusion {occluder.OcclusionEffect:F2}, from {at}";
        }

        // Debug log of what a downed player's phrase sounds like by the time it is playing, on any peer: the clip's
        // own level, the speech source's real volume after occlusion, how far it carries, the mixer it goes through,
        // the tinnitus duck on the main mix and the occluder's state. One line, a moment after PlaySpeechFromTime,
        // so the source has had its occlusion applied.
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
                string who = player.IsYourPlayer ? "me" : "remote";
                string clipName = clip.Clip != null ? clip.Clip.name : "?";
                if (source == null)
                {
                    RevivalDebugLog.LogDebug($"[DownedVoice:trace] {who} {player.ProfileId} {clipName}: no speech source 0.3 s after it started");
                    yield break;
                }

                var audio = source.source1;
                float duck = float.NaN;
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                    MonoBehaviourSingleton<BetterAudio>.Instance.Master.GetFloat(MainMixerVolumeParam, out duck);
                float distance = CameraManager.Exist ? CameraManager.Instance.Distance(source.Position) : float.NaN;

                RevivalDebugLog.LogDebug(
                    $"[DownedVoice:trace] {who} {player.ProfileId} {clipName}: clip vol {clip.Volume:F2} falloff {clip.Falloff} | " +
                    $"source vol {audio.volume:F3} (pre-occlusion {PreOcclusionVolume(source):F3} x occlusion {source.OcclusionVolumeFactor:F2}) " +
                    $"playing {audio.isPlaying} mute {audio.mute} blend {audio.spatialBlend:F1} maxDist {audio.maxDistance:F0} dist {distance:F1} | " +
                    $"mixer '{(audio.outputAudioMixerGroup != null ? audio.outputAudioMixerGroup.name : "none")}' main {duck:F1} dB | {DescribeOcclusion(player)}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedVoice:trace] failed: {ex.Message}");
            }
        }
    }
}
