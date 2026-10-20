//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll ]====================
    // Takes a living remote player's body away from EFT's animation while they are downed, and moves it between two
    // states:
    //   animated - DownedPose plays the fall, then the breathing loop, exactly as authored and bent to the ground.
    //              This is the body whenever nothing is pushing it;
    //   physics  - while dragged: a ragdoll whose muscles (motors inside the physics solver) pull it toward the settle clip's first frame (the muscle
    //              pose: legs and spine straight, arms at the sides), no pins, so gravity, the floor and the drag still
    //              win. The muscles are the same dragged or let go. Let go it falls and they pull it toward that pose;
    //              once it has stopped moving (HasSettled) it goes back to animated, placed where it lies, and plays
    //              the settle clip (which starts on that pose) into the loop. A body that settled rolled more toward its front
    //              than its back goes to EFT's own prone animation instead (PhysicsToProne);
    // When the player takes the body back it rolls onto its front the way it lies, sliding onto where the player is
    // (DownedPose), and only then does everything go back to EFT's animation.
    internal static partial class BodyRagdoll
    {
        //====================[ Tuning ]====================
        // A body lying free (no hold) further than this from the player root (off a ledge, through a gap, or left
        // behind by a drag the real body could not follow) is set down on the root again.
        private const float MaxFreeDrift = 4f;

        // A limb pulled further than this from its joint (m) is snagged on geometry and is put back on it.
        private const float MaxJointStretch = 0.14f;
        // A snagged limb passes through the world for at least this long (s), and at most this long from the
        // first snag even if it is still inside something.
        private const float SnagReleaseMin = 0.3f;
        private const float SnagReleaseMax = 2f;
        // Penetration deeper than this counts as still inside geometry (resting contact is a few mm).
        private const float SnagOverlapTolerance = 0.02f;

        // How hard the ground slows a sliding limb (m/s²); higher makes the legs and heels grip and trail properly
        // along the floor and a falling body slide less.
        private const float GroundFriction = 9.5f;

        // Solver passes per ragdoll body (the game default is 3/1); more keeps the joints tight under the pull.
        private const int SolverIterations = 12;
        private const int SolverVelocityIterations = 4;

        // Forward speed given to a falling body per metre of height (1/s), so it tips over instead of folding down.
        private const float FallTipRate = 1f;

        // The body is a ragdoll, dragged or let go: gravity pulls every part down, the floor holds what lies on it, and
        // every muscle in the body contracts toward the muscle pose at its group setting. The muscles are motors solved
        // inside the physics step (BodyRagdollMuscles), springs and never a servo, so gravity, the floor and the dragger's
        // hands still act on every part, and they are the same held or let go.

        // After a drag the body stays physical until it has settled: every part slower than the settle
        // threshold (SettleSpeed, m/s) and turning slower than SettleSpinPerSpeed times it (rad/s). It then plays the
        // settle clip and goes on to the breathing loop (PhysicsToPose). A body still moving SettleTimeoutSeconds after it
        // was let go counts as settled anyway; MinSettleSeconds gives a body let go in the air time to start falling
        // before it is looked at.
        private const float SettleSpeed = 1f;
        private const float SettleSpinPerSpeed = 5f;
        private const float MinSettleSeconds = 0.3f;
        private const float SettleTimeoutSeconds = 3f;

        // Ragdoll-to-animation blend on exit (seconds).
        private const float BlendOutDuration = 0.35f;

        private const string ContactObjectName = "KMA_RagdollContact";
        // Unity's built-in "Ignore Raycast" layer; in the project's collision matrix it collides with nothing,
        // so a contact collider moved onto it passes through the world while keeping its shape and bounds.
        private const int ReleasedLayer = 2;

        private static readonly Dictionary<string, Session> Active = new Dictionary<string, Session>();
        // Players whose ragdoll could not be built or went bad; not retried until it is no longer wanted.
        private static readonly HashSet<string> Failed = new HashSet<string>();
        // When each of them failed, and how long a pickup waits before trying a ragdoll that failed again.
        private static readonly Dictionary<string, float> FailedAt = new Dictionary<string, float>();
        private const float RetryAfterFailureSeconds = 2f;

        private static void MarkFailed(string profileId)
        {
            Failed.Add(profileId);
            FailedAt[profileId] = Time.time;
        }
        // Each bone's place on its parent as the rig has it (RestoreBoneOffsets), from the first time it was seen.
        private static readonly Dictionary<Transform, Vector3> CleanLocal = new Dictionary<Transform, Vector3>();

        private static PhysicMaterial _contactMaterial;
        // Scratch buffers for UpdateReleased / IsInsideWorld.
        private static readonly List<Collider> _restored = new List<Collider>();
        private static readonly Collider[] _overlaps = new Collider[16];

        //====================[ Public API ]====================
        // Updates the ragdoll pose and, while it is dragged, the dragger's hold on it (drag, null when nobody holds it).
        public static void Sync(Player player, bool want, DragSession drag)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;
            // A headless host has no visuals to ragdoll.
            if (FikaBackendUtils.IsHeadless) return;

            string id = player.ProfileId;
            bool active = Active.TryGetValue(id, out var session);
            if (RevivalDebugLog.IsDebugLogsEnabled) TraceAfterGetUp(player, want, active ? session : null);

            if (active && session.Player == null)
            {
                Active.Remove(id);
                active = false;
            }

            // The game squashes the local Ribcage (and the arms under it) along the view's depth in first person; the arms are
            // ragdoll bodies, whose joints and colliders must not sit under a squashed parent. Held for as long as the body is
            // taken over (released in Exit).
            if (active && session.Player != null && session.Player.IsYourPlayer) FullScaleArms.Want(session.Player, session, !session.Prone);

            if (!want) Failed.Remove(id);

            if (want && !active)
            {
                // A pickup tries again, so a ragdoll that failed earlier does not leave the body without one.
                if (drag != null && (!FailedAt.TryGetValue(id, out float failedAt) || Time.time - failedAt >= RetryAfterFailureSeconds)) Failed.Remove(id);
                if (!Failed.Contains(id)) Enter(player, drag != null);
            }
            else if (want && session.Phase == BodyPhase.Prone)
            {
                // EFT's prone body until a grab takes it back into the physics, or the one who let go of it says it lies
                // on its back.
                if (drag != null) ProneToPhysics(session);
                else if (TryGetDropRest(player, out var st) && st.DropOnBack) ProneToPose(session, st);
            }
            else if (want && session.Phase != BodyPhase.Physics)
            {
                // Animated until a grab hands the body to the physics. A body getting up that is wanted down again
                // drops back to the ground through the physics too, and settles into the loop from there.
                if (drag != null || session.Phase == BodyPhase.Roll) PoseToPhysics(session, drag != null);
                // Lying on its back here, and the one who let go of it says it lies on its front.
                else if (session.Phase == BodyPhase.Pose && !session.Pose.IsPlayingFall
                         && TryGetDropRest(player, out var st) && !st.DropOnBack)
                    PoseToProne(session, st);
            }
            else if (want)
            {
                Drive(session, drag);
            }
            else if (active)
            {
                GetUp(session);
            }
        }

        // A body of this player is taken over (animated or physical); not one lying prone under EFT's own animation.
        public static bool HasSession(string profileId) => Active.TryGetValue(profileId, out var session) && !session.Prone;

        // The animated body underneath the pose (the local player's only; see DownedPose.AnimatedChestAhead).
        public static bool TryGetAnimatedChestAhead(string profileId, out float ahead)
        {
            ahead = 0f;
            if (!Active.TryGetValue(profileId, out var session) || session.Pose == null || !session.Pose.HasAnimatedChest) return false;
            ahead = session.Pose.AnimatedChestAhead;
            return true;
        }

        // The local player is still going down into prone (see Enter).
        private static bool InProneTransit(Player player) =>
            player.IsYourPlayer && player.CurrentStateName == EPlayerState.Transit2Prone;

        // This peer's copy of the body is still playing its fall.
        public static bool IsFalling(string profileId) =>
            Active.TryGetValue(profileId, out var session) && session.Pose != null && session.Pose.IsPlayingFall;

        // The body is a ragdoll now (its bones off the player root), not an animation.
        public static bool IsPhysical(string profileId) =>
            Active.TryGetValue(profileId, out var session) && session.Phase == BodyPhase.Physics;

        public static void ReleaseForDeath(Player player) => EndForPlayer(player, ExitPose.Keep);

        private static void OnDeadOrUnspawn(Player player) => EndForPlayer(player, ExitPose.Rest);

        private static void EndForPlayer(Player player, ExitPose pose)
        {
            if (player == null || string.IsNullOrEmpty(player.ProfileId)) return;

            if (Active.TryGetValue(player.ProfileId, out var session))
                Exit(session, pose);
            Failed.Remove(player.ProfileId);

            var blend = player.GetComponent<BlendOut>();
            if (blend != null)
            {
                // Disabled as well: Destroy only takes effect at the end of the frame.
                blend.enabled = false;
                UnityEngine.Object.Destroy(blend);
            }
        }

        // Raid boundary: the player objects are gone, so there is nothing to restore. The detached ragdoll
        // roots and hand objects live at the scene root and go with the scene.
        public static void ClearAll()
        {
            // Extraction can leave the local player alive across the scene boundary. Exit it through the same path as a
            // revive so detached ragdoll bodies, joints, colliders, the camera, and animator state are all restored.
            var sessions = new List<Session>(Active.Values);
            foreach (var session in sessions)
            {
                if (session.Player == null || !session.Player.IsYourPlayer) continue;
                try { Exit(session, ExitPose.Rest); }
                catch (Exception ex) { Plugin.LogSource.LogWarning($"[BodyRagdoll] Local animation restore failed: {ex.Message}"); }
            }
            Active.Clear();
            Failed.Clear();
            FailedAt.Clear();
            ClipRig.ClearReports();
            SkeletonBind.ClearKnown();
            CleanLocal.Clear();
        }

        //====================[ Enter ]====================
        // Starts the ragdoll, optionally with a drag hold.
        private static void Enter(Player target, bool held)
        {
            // Humans only: the replicated copy of someone else (under Fika an ObservedPlayer) and the local player's own
            // body, which is ragdolled too so the owner's view is the one the observers see.
            if (target.IsAI) return;
            Player player = target;
            var pool = player.GetComponent<PlayerPoolObject>();
            if (pool == null) return;

            // A blend left over from the previous ragdoll would fight the new simulation.
            var staleBlend = player.GetComponent<BlendOut>();
            if (staleBlend != null) UnityEngine.Object.Destroy(staleBlend);

            var session = new Session { Player = player };
            // Registered before anything is mutated so a mid-way failure can be rolled back by Exit.
            Active[player.ProfileId] = session;
            player.OnPlayerDeadOrUnspawn += OnDeadOrUnspawn;

            try
            {
                // The owner's body falls back to ordinary EFT animation when its skeleton cannot play the baked
                // clips. In particular, do this before a drag can suspend the local animator in StartPhysics.
                // ClipRig emits its per-player [ClipRig] warning once; do not turn this expected compatibility
                // failure into a repeating ragdoll error.
                if (player.IsYourPlayer && !DownedPose.CanBuildRig(player))
                {
                    MarkFailed(player.ProfileId);
                    Exit(session, ExitPose.Rest);
                    return;
                }

                // Already down on its front with no fall to play (went down prone, or joined late): EFT's prone body is
                // right as it is, and nothing is taken from EFT. Not for a grab, whose physics starts from it. The local
                // body's fall, when it recorded one, is played first (below), so it is not this.
                if (!held && !DownedPose.IsUpright(player) && DownedPose.FallFor(player) == null
                    && (!player.IsYourPlayer || DownedRagdollController.LocalFallOf(player) == null)
                    && !DownedPose.HasRecordedFall(player)
                    && !DragGrip.IsOnBack(player))
                {
                    session.Prone = true;
                    return;
                }

                CaptureRest(player, session);
                DetachFromEft(player, session);

                if (BodyInteractableRuntime.TryGet(player.ProfileId, out var interactable))
                {
                    session.Interactable = interactable;
                    interactable.SetRagdollInert(true);
                }

                // Going down on its own: the fall, then the loop. Already lying on its back (joined late): the loop.
                // Grabbed at once, or on its front with nothing to play: the physics.
                if (!held)
                {
                    // The local body goes down by the fall it recorded going down (EFT's prone is already on it); an
                    // observer plays the owner's fall (BleedingOutPacket), else works it out from the body it sees.
                    session.Pose = (player.IsYourPlayer ? DownedPose.TryStartLocalFall(player, pool) : DownedPose.TryStartFallFor(player, pool))
                                   ?? DownedPose.TryResume(player, pool);
                    if (session.Pose != null) return;
                }

                // The local body's move into prone is played by the body animator (Transit2ProneState moves and turns the
                // player from its root motion); the physics switches that off, so it waits for the move to finish. Tried
                // again next tick.
                if (InProneTransit(player))
                {
                    Exit(session, ExitPose.Rest);
                    return;
                }

                StartPhysics(session, pool, held, null);
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Enter failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        //====================[ Animated <-> Physics ]====================
        // A grab (held), or a body getting up wanted down again: the animation stops and the physics starts from the
        // pose on screen, carrying on its motion.
        private static void PoseToPhysics(Session session, bool held)
        {
            if (InProneTransit(session.Player)) return;
            var pose = session.Pose;
            session.Pose = null;
            try
            {
                pose.Stop();
                StartPhysics(session, session.Player.GetComponent<PlayerPoolObject>(), held, pose);
            }
            catch (Exception ex)
            {
                MarkFailed(session.Player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to physics failed for {session.Player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // How long a hand-back to EFT's prone keeps the ragdoll's pose on screen while EFT's animator, which was off, runs
        // underneath (s): the roll's length, the same time WarmAnimator gives it there, so it has played its move into prone
        // when the pose eases out.
        private static float ProneWarmSeconds => BakedClip.GetUp?.Duration ?? 1.5f;

        // Back to animation where the body lies. Rolled more toward its back than its front (a tie counts as the back): the
        // settle clip into the breathing loop, blended in from the pose on screen. Otherwise it goes to EFT's prone.
        private static void PhysicsToPose(Session session)
        {
            var player = session.Player;
            // Where it came to rest, read while the ragdoll still is one: if this client let go of it, it says so to everyone.
            bool settledOnBack = DragGrip.IsOnBack(player);
            if (TryGetRest(player.ProfileId, out Vector3 restPelvis, out float restSpine))
                DownedDragController.OnBodySettled(player.ProfileId, restPelvis, restSpine, settledOnBack);

            // The way up the one who let go of it says it lies, when said (it may be this client); else as it settled here.
            bool declared = TryGetDropRest(player, out var st);
            if (!(declared ? st.DropOnBack : settledOnBack))
            {
                PhysicsToProne(session);
                return;
            }

            var pool = player.GetComponent<PlayerPoolObject>();
            try
            {
                StopPhysics(session, true);
                // Settled on its front here but to lie on its back: the loop, turned and slid onto the declared place.
                session.Pose = settledOnBack
                    ? DownedPose.TrySettle(player, pool)
                    : DownedPose.TryStartLyingAt(player, pool, st.DropRestPelvis, st.DropFacingYaw);
                if (session.Pose != null) return;

                // Nothing to go back to: physical as before (lying still, so not tipped over).
                StartPhysics(session, pool, false, null, tip: false);
                session.StayPhysical = true;
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to animation failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // A body that settled on its front goes to EFT's prone animation where it lies: the root is turned to the way the body
        // points (an observer's was left behind while the body was taken, and EFT draws the prone body from it), the bones are
        // given back, and the pose they have is held while the animator runs its move into prone out of sight, then eased out
        // of. The session stays, marked prone, so the body is not taken again until a grab.
        private static void PhysicsToProne(Session session)
        {
            var player = session.Player;
            Vector3 level = default;
            // The declared spine when the one who let go of it has said; else the way it settled here.
            bool declared = TryGetDropRest(player, out var st);
            bool aimed = declared || (TryGetFront(session, out _, out Vector3 spine)
                                      && global::KeepMeAlive.Animation.Shared.MathUtil.TryLevel(spine, out level));
            float yaw = declared ? st.DropSpineYaw : aimed ? global::KeepMeAlive.Animation.Shared.MathUtil.YawOf(level) : 0f;
            try
            {
                // Back under the player first: TurnRoot turns the skeleton's top bones, which the ragdoll has detached.
                StopPhysics(session, true);
                if (aimed) TurnRoot(player, yaw);
                HandBack(session, ExitPose.Blend, player.IsYourPlayer ? 0f : ProneWarmSeconds);
                session.Prone = true;
                RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId} settled on its front: prone (yaw {yaw:F0})");
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to prone failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // The place the one who let go of the body said it came to rest (DropRestPacket), when it has said.
        private static bool TryGetDropRest(Player player, out RMPlayer st) =>
            RMSession.TryGetPlayerState(player.ProfileId, out st) && st.HasDropRest;

        // Lying on its back here, but the one who let go of it says it lies on its front: to EFT's prone, the root turned to
        // the declared spine (the way PhysicsToProne hands a body that settled on its front over).
        private static void PoseToProne(Session session, RMPlayer st)
        {
            var player = session.Player;
            try
            {
                TurnRoot(player, st.DropSpineYaw);
                HandBack(session, ExitPose.Blend, player.IsYourPlayer ? 0f : ProneWarmSeconds);
                session.Prone = true;
                RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId} lies on its front as declared: prone (yaw {st.DropSpineYaw:F0})");
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to prone failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // Lying prone here, but the one who let go of it says it lies on its back: taken back from EFT (as a grab does) and
        // put into the loop, turned and slid onto the declared place.
        private static void ProneToPose(Session session, RMPlayer st)
        {
            var player = session.Player;
            if (InProneTransit(player)) return;
            try
            {
                // A blend left from the hand-back would fight the pose.
                var blend = player.GetComponent<BlendOut>();
                if (blend != null)
                {
                    blend.enabled = false;
                    UnityEngine.Object.Destroy(blend);
                }
                session.Prone = false;
                if (session.Rest.Count == 0) CaptureRest(player, session);
                DetachFromEft(player, session);
                if (session.Interactable != null) session.Interactable.SetRagdollInert(true);

                var pool = player.GetComponent<PlayerPoolObject>();
                session.Pose = DownedPose.TryStartLyingAt(player, pool, st.DropRestPelvis, st.DropFacingYaw);
                if (session.Pose != null)
                {
                    RevivalDebugLog.LogDebug($"[BodyRagdoll] {player.ProfileId} lies on its back as declared");
                    return;
                }

                // Nothing to play: physical, lying still (as PhysicsToPose has it).
                StartPhysics(session, pool, false, null, tip: false);
                session.StayPhysical = true;
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Taking a prone body onto its back failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // A grab on a body lying prone: the physics starts from the pose on screen, as for a body grabbed at once.
        private static void ProneToPhysics(Session session)
        {
            var player = session.Player;
            if (InProneTransit(player)) return;
            try
            {
                // A blend left from the hand-back would fight the simulation.
                var blend = player.GetComponent<BlendOut>();
                if (blend != null)
                {
                    blend.enabled = false;
                    UnityEngine.Object.Destroy(blend);
                }
                session.Prone = false;
                if (session.Rest.Count == 0) CaptureRest(player, session);
                DetachFromEft(player, session);
                if (session.Interactable != null) session.Interactable.SetRagdollInert(true);
                StartPhysics(session, player.GetComponent<PlayerPoolObject>(), true, null);
            }
            catch (Exception ex)
            {
                MarkFailed(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Grab of a prone body failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // Whether a body let go has come to rest: every part slower than the threshold, in speed and in spin.
        private static bool HasSettled(Session session)
        {
            float free = Time.time - session.FreeSince;
            if (free < MinSettleSeconds) return false;

            float speed = SettleSpeed;
            float spin = speed * SettleSpinPerSpeed;
            float fastest = 0f, fastestSpin = 0f;
            string fastestName = "", spinName = "";
            foreach (var state in session.Bodies)
            {
                float v = state.Body.velocity.magnitude, w = state.Body.angularVelocity.magnitude;
                if (v > fastest) { fastest = v; fastestName = state.Body.name; }
                if (w > fastestSpin) { fastestSpin = w; spinName = state.Body.name; }
            }
            bool settled = (fastest <= speed && fastestSpin <= spin) || free >= SettleTimeoutSeconds;
            TraceSettle(session, free, fastest, fastestName, speed, fastestSpin, spinName, spin, settled);
            return settled;
        }

        // Builds and starts the simulation. from: the animation that played until now, whose motion the bodies carry
        // on.
        private static void StartPhysics(Session session, PlayerPoolObject pool, bool held, DownedPose from, bool tip = true)
        {
            var player = session.Player;
            // The muscle pose (the settle clip's first frame, lying on the back): where the joints and the muscle motors are
            // zeroed, and the one pose every muscle pulls toward, held or let go.
            SkeletonCopy pose = BuildMusclePose(player);

            SuspendLocalAnimation(player, session);
            if (player.IsYourPlayer && session.View == null) session.View = LocalBodyCamera.Attach(player);
            SpawnPhysics(pool, session);
            if (session.Chest == null || session.LeftShoulder == null || session.RightShoulder == null)
                throw new InvalidOperationException("ragdoll has no chest or shoulder bodies");
            ZeroJointsAtMusclePose(player, session, pose);
            ActivateBodies(session);
            SeatOnJoints(session);
            try
            {
                BuildMuscles(session, pose);
            }
            catch (Exception ex)
            {
                // Without muscle tone the body is just limp; not worth giving up the ragdoll for.
                session.Muscles.Clear();
                session.NonPhysicalBones.Clear();
                Plugin.LogSource.LogWarning($"[BodyRagdoll] No muscle tone for {player.ProfileId}: {ex.Message}");
            }
            session.FreeSince = held ? -1f : Time.time;
            DetachFromRoot(session);

            if (from != null) CarryMotion(session, from);
            else if (!held && tip) TipForward(player, session);

            if (RevivalDebugLog.IsDebugLogsEnabled) Plugin.LogSource.LogInfo($"[BodyRagdoll] Entered for {player.ProfileId} (living drag/settle ragdoll, {session.Bodies.Count} bodies, {session.Joints.Count} joints, {session.Muscles.Count} muscles, {session.Contacts.Count} contacts, {session.Tops.Count} roots{(from != null ? ", from the animation" : "")})");
        }

        // Starts each body moving the way its bone was moving in the animation.
        private static void CarryMotion(Session session, DownedPose pose)
        {
            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                if (!pose.TryGetVelocity(body.transform, out var velocity, out var angularVelocity)) continue;
                body.velocity = velocity;
                body.angularVelocity = angularVelocity;
            }
        }

        // Creates (idempotently) and arms the joints and rigidbodies, the way CorpseRagdoll.Start does, with realistic
        // human segment masses so the limbs do not outweigh the trunk.
        private static void SpawnPhysics(PlayerPoolObject pool, Session session)
        {
            PhysicMaterial material = ContactMaterial();

            foreach (var spawner in pool.RigidbodySpawners)
            {
                Rigidbody body = spawner.Create();
                var part = spawner.GetComponent<BodyPartCollider>().BodyPartColliderType;

                session.Bodies.Add(new RigidbodyState
                {
                    Body = body,
                    WasKinematic = body.isKinematic,
                    CollisionMode = body.collisionDetectionMode,
                    MaxDepenetrationVelocity = body.maxDepenetrationVelocity,
                    SleepThreshold = body.sleepThreshold,
                    SolverIterations = body.solverIterations,
                    SolverVelocityIterations = body.solverVelocityIterations,
                    MaxAngularVelocity = body.maxAngularVelocity,
                    MaxLinearVelocity = body.maxLinearVelocity,
                    WasMass = body.mass,
                    WasDrag = body.drag,
                    WasAngularDrag = body.angularDrag,
                    Part = part
                });

                body.mass = HumanSegmentMass(part);
                session.BodyByPart[part] = body;
                AddContact(body, part, material, session);

                // The dragger holds the body by the shoulders of the upper chest.
                switch (part)
                {
                    case EBodyPartColliderType.RibcageUp: session.Chest = body; break;
                    case EBodyPartColliderType.LeftUpperArm: session.LeftShoulder = body; break;
                    case EBodyPartColliderType.RightUpperArm: session.RightShoulder = body; break;
                    case EBodyPartColliderType.Pelvis: session.Pelvis = body; break;
                }

                // The game steps physics manually; only while a registered rigidbody exists does it run.
                PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
            }

            IgnoreInternalCollisions(session);

            foreach (var spawner in pool.JointSpawners)
            {
                // CharacterJointSpawner always creates a CharacterJoint.
                var joint = (CharacterJoint)spawner.Create();
                var own = joint.GetComponent<Rigidbody>();

                session.Joints.Add(new JointState
                {
                    Joint = joint,
                    Body = own,
                    EnablePreprocessing = joint.enablePreprocessing,
                    MassScale = joint.massScale,
                    ConnectedMassScale = joint.connectedMassScale,
                    EnableProjection = joint.enableProjection,
                    Axis = joint.axis,
                    SwingAxis = joint.swingAxis,
                    LowTwistLimit = joint.lowTwistLimit,
                    HighTwistLimit = joint.highTwistLimit,
                    Swing1Limit = joint.swing1Limit,
                    Swing2Limit = joint.swing2Limit,
                    TwistLimitSpring = joint.twistLimitSpring,
                    SwingLimitSpring = joint.swingLimitSpring
                });

                // Held at the prefab's anchors. EFT sets the joints up with autoConfigureConnectedAnchor on, so Unity
                // works the connected anchor out again from wherever the bones are each time the joint is set up
                // (ZeroJointsAtMusclePose sets it up afresh every pickup): from a body the last drag left out of shape, that
                // moved the shoulders a little further round each time, until they met behind the neck.
                joint.autoConfigureConnectedAnchor = false;
                joint.anchor = spawner.anchor;
                joint.connectedAnchor = spawner.connectedAnchor;

                // CorpseRagdoll.Start: no preprocessing, projection on so a stretched joint is pulled back
                // instead of springing, mass-ratio scaling using the corrected human segment masses.
                joint.enablePreprocessing = false;
                joint.enableProjection = true;
                joint.projectionDistance = 0.05f;
                joint.projectionAngle = 10f;
                // The ratio of the prefab's own masses, as CorpseRagdoll.Start has it, not of the human masses set above: a light child
                // with a big scale reacts to its own joint's limits and anchors that many times harder in the solver (massScale scales
                // the inverse mass for constraints), and with the human masses the upper arms got 5.8 (the prefab's give far less). The
                // arms and forearms bounced off their twist limits every tick (log 2026-10-18).
                joint.massScale = PrefabMass(session, joint.connectedBody) / PrefabMass(session, own);
                joint.connectedMassScale = 1f;
            }

            AddLimbSpacers(session, material);
        }

        // The prefab mass before I set human masses, or the current mass for bodies I do not manage.
        private static float PrefabMass(Session session, Rigidbody body)
        {
            foreach (var state in session.Bodies)
                if (state.Body == body) return state.WasMass;
            return body.mass;
        }

        // Makes the bodies dynamic, as CorpseRagdoll.Start does. Separate from creating them because the joints are zeroed first
        // (ZeroJointsAtMusclePose runs the physics for a moment, which must find every body still kinematic).
        private static void ActivateBodies(Session session)
        {
            float maxDepenetration = EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity;
            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                body.isKinematic = false;
                body.maxDepenetrationVelocity = maxDepenetration;
                // Continuous against the static world: a limb whipped round by a turning dragger is fast and
                // small enough to step through a thin floor otherwise.
                body.collisionDetectionMode = CollisionDetectionMode.Continuous;
                body.solverIterations = SolverIterations;
                body.solverVelocityIterations = SolverVelocityIterations;
                // A body that falls asleep stops following the hold.
                body.sleepThreshold = 0f;
                // Unity's own spin cap (default 7) would otherwise sit under MaxPartSpin.
                body.maxAngularVelocity = MaxPartSpin;
                body.drag = 0.35f;
                body.angularDrag = 1.6f;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        // Tips the body forward as it begins to fall.
        private static void TipForward(Player player, Session session)
        {
            if (!global::KeepMeAlive.Animation.Shared.MathUtil.TryLevel(player.Transform.forward, out Vector3 forward)) return;

            float feet = player.Position.y;
            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                float height = Mathf.Max(0f, body.position.y - feet);
                body.velocity = forward * (FallTipRate * height);
            }
        }

        //====================[ Detach From Root ]====================
        // Takes the top of the ragdoll (every rigidbody with no ragdoll rigidbody above it - normally just the
        // pelvis) out from under the player, so the network moving the player root no longer drags the bones.
        // World pose is kept; the skinned meshes follow their bones wherever they are parented.
        private static void DetachFromRoot(Session session)
        {
            var bodies = new HashSet<Transform>();
            foreach (var state in session.Bodies)
                if (state.Body != null) bodies.Add(state.Body.transform);

            foreach (var t in bodies)
            {
                bool top = true;
                for (var p = t.parent; p != null; p = p.parent)
                {
                    if (bodies.Contains(p)) { top = false; break; }
                }
                if (!top) continue;

                session.Tops.Add(new Detached { Transform = t, Parent = t.parent, SiblingIndex = t.GetSiblingIndex() });
                t.SetParent(null, true);
            }
        }

        private static void ReattachToRoot(Session session)
        {
            foreach (var top in session.Tops)
            {
                top.Transform.SetParent(top.Parent, true);
                top.Transform.SetSiblingIndex(top.SiblingIndex);
            }
            session.Tops.Clear();
        }

        //====================[ Drive ]====================
        // Advances the ragdoll physics one tick: held by the dragger's hands (BodyRagdollHold), or lying free, when it
        // settles and then plays the settle clip into the breathing loop.
        private static void Drive(Session session, DragSession drag)
        {
            var player = session.Player;
            Vector3 chestPosition = session.Chest.position;
            if (!chestPosition.IsFinite())
            {
                AbortRagdoll(session, "simulation produced an invalid position", ExitPose.Rest);
                return;
            }

            // The revive/heal proxies are attached by a coroutine and may only appear after the ragdoll started.
            if (session.Interactable == null && BodyInteractableRuntime.TryGet(player.ProfileId, out var interactable))
            {
                session.Interactable = interactable;
                interactable.SetRagdollInert(true);
            }

            // The game runs one physics step per frame, capped in length: on a slow frame the body only simulates
            // the capped time, so the hand acts over that too.
            float step = PhysicsStep();

            if (drag != null) IgnoreDragger(session, drag);
            else RestoreDragger(session);
            FixSnags(session);
            UpdateReleased(session);
            ApplySpeedLimits(session);
            UpdateMuscleDrives(session);
            PoseLeafBones(session);
            TraceMuscles(session, step);

            if (drag == null)
            {
                if (session.Hand != null) DestroyHand(session);
                if (session.FreeSince < 0f) session.FreeSince = Time.time;
                if ((chestPosition - player.Position).sqrMagnitude > MaxFreeDrift * MaxFreeDrift)
                    ReseatOnRoot(session);
                // Settled: on to the settle clip where it lies (a body on its front stays physical instead).
                else if (!session.StayPhysical && HasSettled(session))
                    PhysicsToPose(session);
                return;
            }
            session.FreeSince = -1f;
            session.StayPhysical = false;

            DriveHold(session, drag, step);
        }


        // The length of the physics step the game takes this frame. Not the frame's own length: the game steps once a frame by the average of
        // the last frames' lengths, capped (PhysicsExtensions.Simulation.SmoothSimulate), so a muscle worked out for this frame's length
        // is wrong by as much as the frame times swing (10-20 ms in the logs) and a stiff spring rings.
        private static float PhysicsStep()
        {
            float average = (float)PhysicsExtensions.Simulation.SimulationAVGDeltaTime;
            if (!(average > 0f)) average = Time.deltaTime;
            return Mathf.Min(average, EFTHardSettings.Instance.MaxSmoothPhysicsDeltaTime);
        }

        //====================[ Exit ]====================
        // Ends the simulation: every body back to how EFT had it (kinematic, its joints as they were), under the
        // player again with the pose on screen kept. still: stop the bodies (not for a corpse, whose ragdoll carries
        // on with their motion).
        private static void StopPhysics(Session session, bool still)
        {
            DestroyHand(session);
            DestroyMotors(session);

            foreach (var contact in session.Contacts)
                UnityEngine.Object.Destroy(contact.gameObject);
            session.Contacts.Clear();
            foreach (var spacer in session.Spacers)
            {
                // Off at once: Destroy waits for the end of the frame.
                spacer.enabled = false;
                UnityEngine.Object.Destroy(spacer.gameObject);
            }
            session.Spacers.Clear();
            session.Released.Clear();
            RestoreDragger(session);

            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                // Velocity can only be cleared while dynamic; kinematic bodies reject the write.
                if (!body.isKinematic && still)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                // Collision mode first: a body going kinematic must not be left on continuous.
                body.collisionDetectionMode = state.CollisionMode;
                body.isKinematic = state.WasKinematic;
                body.maxDepenetrationVelocity = state.MaxDepenetrationVelocity;
                body.sleepThreshold = state.SleepThreshold;
                body.solverIterations = state.SolverIterations;
                body.solverVelocityIterations = state.SolverVelocityIterations;
                body.maxAngularVelocity = state.MaxAngularVelocity;
                body.maxLinearVelocity = state.MaxLinearVelocity;
                body.mass = state.WasMass;
                body.drag = state.WasDrag;
                body.angularDrag = state.WasAngularDrag;
            }

            foreach (var state in session.Joints)
            {
                state.Joint.enablePreprocessing = state.EnablePreprocessing;
                state.Joint.enableProjection = state.EnableProjection;
                state.Joint.massScale = state.MassScale;
                state.Joint.connectedMassScale = state.ConnectedMassScale;
                state.Joint.axis = state.Axis;
                state.Joint.swingAxis = state.SwingAxis;
                state.Joint.lowTwistLimit = state.LowTwistLimit;
                state.Joint.highTwistLimit = state.HighTwistLimit;
                state.Joint.swing1Limit = state.Swing1Limit;
                state.Joint.swing2Limit = state.Swing2Limit;
                state.Joint.twistLimitSpring = state.TwistLimitSpring;
                state.Joint.swingLimitSpring = state.SwingLimitSpring;
            }

            // Back under the player, keeping the world pose so whatever comes next starts from what is on screen,
            // with every bone back on its parent where the rig has it (the stretch dropped). Only after a simulation: it
            // is what stretched them, and the tops it detached are what keep the pelvis out of this. A body that was only
            // animated has none, and this would put its pelvis back at the standing height, lifting it off the ground.
            if (still && session.Bodies.Count > 0) RestoreBoneOffsets(session);
            ReattachToRoot(session);

            ResumeLocalAnimation(session.Player, session);
            if (session.View != null)
            {
                session.View.enabled = false;
                UnityEngine.Object.Destroy(session.View);
                session.View = null;
            }
            session.Bodies.Clear();
            session.BodyByPart.Clear();
            session.Joints.Clear();
            session.Muscles.Clear();
            session.NonPhysicalBones.Clear();
            session.Chest = session.LeftShoulder = session.RightShoulder = session.Pelvis = null;
            session.RollArmed = session.Rolled = false;
            session.FreeSince = -1f;
            session.StayPhysical = false;
        }

        // Evaluates EFT's body animator once and gives the blend the pose it leaves as its target. Without an
        // evaluation the target is the pose on screen, and the blend does nothing.
        private static void BlendTowardAnimator(Player player, BlendOut blend)
        {
            try
            {
                player.BodyAnimatorCommon.Update(0f);
                blend.CaptureTargets();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[BodyRagdoll] Animator evaluation failed for {player.ProfileId}: {ex.Message}");
            }
        }

        // The two upper arm bones of the rig, the roots of everything the arms pose.
        private static List<Transform> ArmRoots(Session session)
        {
            var roots = new List<Transform>();
            foreach (var rest in session.Rest)
                if (rest.Bone != null && (rest.Bone.name == BoneNames.LeftUpperArm || rest.Bone.name == BoneNames.RightUpperArm))
                    roots.Add(rest.Bone);
            return roots;
        }

        private static bool IsUnder(Transform bone, List<Transform> roots)
        {
            foreach (var root in roots)
                if (bone.IsChildOf(root)) return true;
            return false;
        }

        private static void Exit(Session session, ExitPose pose = ExitPose.Blend)
        {
            var player = session.Player;
            Active.Remove(player.ProfileId);
            player.OnPlayerDeadOrUnspawn -= OnDeadOrUnspawn;
            if (player.IsYourPlayer) FullScaleArms.Want(player, session, false);

            // Lying prone under EFT's animation: everything was given back when it went there.
            if (session.Prone) return;
            HandBack(session, pose, 0f);
        }

        // Gives the body back to EFT, however it was taken (the end of Exit, and the hand to prone, which keeps the session).
        // blendDelay: how long the blend holds the pose on screen before easing out of it (s).
        private static void HandBack(Session session, ExitPose pose, float blendDelay)
        {
            var player = session.Player;
            try
            {
                // Animated: stops where it is (for a death the corpse ragdoll carries on from there).
                if (session.Pose != null)
                {
                    session.Pose.Stop();
                    session.Pose = null;
                }

                StopPhysics(session, pose != ExitPose.Keep);

                if (session.Interactable != null) session.Interactable.SetRagdollInert(false);

                // Positions as well as rotations: the joints stretch a little while simulating, and that stretch
                // sits in every bone's local position, not just the root's.
                BlendOut blend = null;
                if (pose == ExitPose.Blend)
                {
                    blend = player.gameObject.AddComponent<BlendOut>();
                    blend.Delay = blendDelay;
                    var arms = player.IsYourPlayer ? ArmRoots(session) : null;
                    foreach (var rest in session.Rest)
                    {
                        if (rest.Bone == null) continue;
                        // The local arms are the first person arms: whatever the game or the revive hands pose there is
                        // theirs from the first frame, not eased in from the downed pose.
                        if (arms != null && IsUnder(rest.Bone, arms)) continue;
                        blend.Capture(rest.Bone);
                    }
                    blend.Trace = player.PlayerBones.Pelvis.Original;
                    var bodyAnimator = player.BodyAnimatorCommon;
                    blend.AnimatorOn = () => bodyAnimator.enabled;
                }
                else if (pose == ExitPose.Rest)
                {
                    foreach (var rest in session.Rest)
                    {
                        if (rest.Bone == null) continue;
                        rest.Bone.localPosition = rest.LocalPosition;
                        rest.Bone.localRotation = rest.LocalRotation;
                    }
                }
                // Keep: the corpse ragdoll carries on from the pose as it is.

                // With the animator back on it rewrites every bone transform next frame; BlendOut eases into it.
                ReattachToEft(player, session);

                // A target for the blend that does not depend on the animator rewriting the bones before it does.
                // (The local animator has run all along, or been resumed above, so its bones are already the target.)
                if (blend != null && !player.IsYourPlayer) BlendTowardAnimator(player, blend);

                RevivalDebugLog.LogDebug($"[BodyRagdoll] Exited for {player.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyRagdoll] Exit failed: {ex}");
            }
        }
    }
}
