//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll ]====================
    // Takes a living remote player's body away from EFT's animation while they are downed, and moves it between two
    // states:
    //   animated - DownedPose plays the fall, then the breathing loop, exactly as authored and bent to the ground.
    //              This is the body whenever nothing is pushing it;
    //   physics  - while dragged: a ragdoll whose muscles pull it toward the settle clip's first frame (the muscle
    //              pose: legs and spine straight, arms at the sides), no pins, so gravity, the floor and the drag still
    //              win. The muscles are the same dragged or let go. Let go it falls and they pull it toward that pose;
    //              once it has stopped moving (HasSettled) it goes back to animated, placed where it lies, and plays
    //              the settle clip (which starts on that pose) into the loop.
    // When the player takes the body back it rolls onto its front the way it lies, sliding onto where the player is
    // (DownedPose), and only then does everything go back to EFT's animation.
    internal static partial class BodyRagdoll
    {
        //====================[ Tuning ]====================
        // A body lying free (no hold) further than this from the player root (off a ledge, through a gap, or left
        // behind by a drag the real body could not follow) is set down on the root again.
        private const float MaxFreeDrift = 4f;

        // A limb pulled further than this from its joint (m) is snagged on geometry and is put back on it.
        private const float MaxJointStretch = 0.08f;
        // A snagged limb passes through the world for at least this long (s), and at most this long from the
        // first snag even if it is still inside something.
        private const float SnagReleaseMin = 0.3f;
        private const float SnagReleaseMax = 2f;
        // Penetration deeper than this counts as still inside geometry (resting contact is a few mm).
        private const float SnagOverlapTolerance = 0.02f;

        // How hard the ground slows a sliding limb (m/s²); ~6 is ordinary floor friction. Higher makes the legs
        // and arms drag and trail more and a falling body slide less.
        private const float GroundFriction = 6f;

        // Solver passes per ragdoll body (the game default is 3/1); more keeps the joints tight under the pull.
        private const int SolverIterations = 12;
        private const int SolverVelocityIterations = 4;

        // Forward speed given to a falling body per metre of height (1/s), so it tips over instead of folding down.
        private const float FallTipRate = 1f;

        // The body is a ragdoll, dragged or let go: gravity pulls every part down, the floor holds what lies on it, and
        // every muscle in the body (spine, neck, head, arms and legs alike) contracts toward the muscle pose at the Muscle
        // Strength setting (KeepMeAliveSettings.MUSCLE_STRENGTH, the spring's stiffness against everything the joint
        // moves). They are forces, never a servo, so gravity, the floor and the dragger's hands still act on every part.
        // The joints keep the prefab's limits. The damping is HoldDampingRatio of critical. A spring this stiff shakes
        // the limbs apart if it is too stiff for the step (it is applied once a frame), so its stiffness is held under
        // StableStepProduct over step squared.
        private const float HoldDampingRatio = 0.85f;
        private const float StableStepProduct = 0.04f;

        // After a drag the body stays physical until it has settled: every part slower than the Settle Movement
        // Threshold (KeepMeAliveSettings.SETTLE_SPEED, m/s) and turning slower than SettleSpinPerSpeed times it (rad/s).
        // It then plays the settle clip and goes on to the breathing loop (PhysicsToPose). There is no time limit;
        // MinSettleSeconds only gives a body let go in the air time to start falling before it is looked at.
        private const float SettleSpinPerSpeed = 5f;
        private const float MinSettleSeconds = 0.3f;

        // Ragdoll-to-animation blend on exit (seconds).
        private const float BlendOutDuration = 0.35f;

        private const string ContactObjectName = "KMA_RagdollContact";
        // Unity's built-in "Ignore Raycast" layer; in the project's collision matrix it collides with nothing,
        // so a contact collider moved onto it passes through the world while keeping its shape and bounds.
        private const int ReleasedLayer = 2;

        private static readonly Dictionary<string, Session> Active = new Dictionary<string, Session>();
        // Players whose ragdoll could not be built or went bad; not retried until it is no longer wanted.
        private static readonly HashSet<string> Failed = new HashSet<string>();
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

            if (!want) Failed.Remove(id);

            if (want && !active)
            {
                // A pickup tries again, so a ragdoll that failed earlier does not leave the body without one.
                if (drag != null) Failed.Remove(id);
                if (!Failed.Contains(id)) Enter(player, drag != null);
            }
            else if (want && session.Phase != BodyPhase.Physics)
            {
                // Animated until a grab hands the body to the physics. A body getting up that is wanted down again
                // drops back to the ground through the physics too, and settles into the loop from there.
                if (drag != null || session.Phase == BodyPhase.Roll) PoseToPhysics(session, drag != null);
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
            Active.Clear();
            Failed.Clear();
            CleanLocal.Clear();
        }

        //====================[ Enter ]====================
        // Starts the ragdoll, optionally with a drag hold.
        private static void Enter(Player target, bool held)
        {
            // Only ever the replicated copy of someone else (under Fika always an ObservedPlayer); the local
            // body is moved, not ragdolled.
            if (target.IsYourPlayer || target.IsAI) return;
            if (!(target is ObservedPlayer player)) return;
            var pool = player.GetComponent<PlayerPoolObject>();

            // A blend left over from the previous ragdoll would fight the new simulation.
            var staleBlend = player.GetComponent<BlendOut>();
            if (staleBlend != null) UnityEngine.Object.Destroy(staleBlend);

            var session = new Session { Player = player };
            // Registered before anything is mutated so a mid-way failure can be rolled back by Exit.
            Active[player.ProfileId] = session;
            player.OnPlayerDeadOrUnspawn += OnDeadOrUnspawn;

            try
            {
                CaptureRest(player, session);
                DetachFromEft(player, session);

                if (BodyInteractableRuntime.TryGet(player.ProfileId, out var interactable))
                {
                    session.Interactable = interactable;
                    interactable.SetRagdollInert(true);
                }

                // Going down on its own: the fall, then the loop. Already lying on its back (joined late): the loop.
                // On its front or grabbed at once: the physics.
                if (!held)
                {
                    session.Pose = DownedPose.TryStartFall(player, pool) ?? DownedPose.TryResume(player, pool);
                    if (session.Pose != null) return;
                }

                StartPhysics(session, pool, held, null);
            }
            catch (Exception ex)
            {
                Failed.Add(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Enter failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        //====================[ Animated <-> Physics ]====================
        // A grab (held), or a body getting up wanted down again: the animation stops and the physics starts from the
        // pose on screen, carrying on its motion.
        private static void PoseToPhysics(Session session, bool held)
        {
            var pose = session.Pose;
            session.Pose = null;
            try
            {
                pose.Stop();
                StartPhysics(session, session.Player.GetComponent<PlayerPoolObject>(), held, pose);
            }
            catch (Exception ex)
            {
                Failed.Add(session.Player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to physics failed for {session.Player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // True when the body lies on its back, so it can go back to the animation. On its front it stays physical (held in
        // the muscle pose) until it is next picked up; the pickup turns it over.
        private static bool CanReturnToPose(Session session)
        {
            if (DragGrip.IsFaceUp(session.Player)) return true;
            session.StayPhysical = true;
            RevivalDebugLog.LogDebug($"[BodyRagdoll] {session.Player.ProfileId} let go on its front: stays physical");
            return false;
        }

        // Back to animation where the body lies, on its back: the settle clip into the loop, blended in from the pose on
        // screen.
        private static void PhysicsToPose(Session session)
        {
            var player = session.Player;
            // The settle can roll a body over.
            if (!CanReturnToPose(session)) return;

            var pool = player.GetComponent<PlayerPoolObject>();
            try
            {
                StopPhysics(session, true);
                session.Pose = DownedPose.TrySettle(player, pool);
                if (session.Pose != null) return;

                // Nothing to go back to: physical as before (lying still, so not tipped over).
                StartPhysics(session, pool, false, null, tip: false);
                session.StayPhysical = true;
            }
            catch (Exception ex)
            {
                Failed.Add(player.ProfileId);
                Plugin.LogSource.LogError($"[BodyRagdoll] Handoff to animation failed for {player.ProfileId}: {ex}");
                Exit(session, ExitPose.Rest);
            }
        }

        // Whether a body let go has come to rest: every part slower than the threshold, in speed and in spin.
        private static bool HasSettled(Session session)
        {
            float free = Time.time - session.FreeSince;
            if (free < MinSettleSeconds) return false;

            float speed = KeepMeAliveSettings.SETTLE_SPEED.Value;
            float spin = speed * SettleSpinPerSpeed;
            float fastest = 0f, fastestSpin = 0f;
            string fastestName = "", spinName = "";
            foreach (var state in session.Bodies)
            {
                float v = state.Body.velocity.magnitude, w = state.Body.angularVelocity.magnitude;
                if (v > fastest) { fastest = v; fastestName = state.Body.name; }
                if (w > fastestSpin) { fastestSpin = w; spinName = state.Body.name; }
            }
            bool settled = fastest <= speed && fastestSpin <= spin;
            TraceSettle(session, free, fastest, fastestName, speed, fastestSpin, spinName, spin, settled);
            return settled;
        }

        // Builds and starts the simulation. from: the animation that played until now, whose motion the bodies carry
        // on.
        private static void StartPhysics(Session session, PlayerPoolObject pool, bool held, DownedPose from, bool tip = true)
        {
            var player = session.Player;
            // The pose the muscles pull toward and the joints zero at: the settle clip's first frame, with no other source.
            // Without it there is no ragdoll (the failure is logged by whoever started this).
            SkeletonCopy musclePose = MusclePose(player);

            SpawnPhysics(pool, session);
            ZeroJointsAtMusclePose(player, session, musclePose);
            ActivateBodies(session);
            SeatOnJoints(session);
            if (session.Chest == null || session.LeftShoulder == null || session.RightShoulder == null)
                throw new InvalidOperationException("ragdoll has no chest or shoulder bodies");
            try
            {
                BuildMuscles(session, musclePose);
            }
            catch (Exception ex)
            {
                // Without muscle tone the body is just limp; not worth giving up the ragdoll for.
                session.Muscles.Clear();
                Plugin.LogSource.LogWarning($"[BodyRagdoll] No muscle tone for {player.ProfileId}: {ex.Message}");
            }
            session.FreeSince = held ? -1f : Time.time;
            DetachFromRoot(session);

            if (from != null) CarryMotion(session, from);
            else if (!held && tip) TipForward(player, session);

            if (RevivalDebugLog.IsDebugLogsEnabled) Plugin.LogSource.LogInfo($"[BodyRagdoll] Entered for {player.ProfileId} (muscles pull toward the settle clip's first frame, {session.Bodies.Count} bodies, {session.Joints.Count} joints, {session.Muscles.Count} muscles, {session.Contacts.Count} contacts, {session.Tops.Count} roots{(from != null ? ", from the animation" : "")})");
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

        // Creates (idempotently) and arms the joints and rigidbodies, the way CorpseRagdoll.Start does.
        private static void SpawnPhysics(PlayerPoolObject pool, Session session)
        {
            PhysicMaterial material = ContactMaterial();

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
                    EnableProjection = joint.enableProjection
                });

                // Held at the prefab's anchors. EFT sets the joints up with autoConfigureConnectedAnchor on, so Unity
                // works the connected anchor out again from wherever the bones are each time the joint is set up
                // (ZeroJointsAtMusclePose sets it up afresh every pickup): from a body the last drag left out of shape, that
                // moved the shoulders a little further round each time, until they met behind the neck.
                joint.autoConfigureConnectedAnchor = false;
                joint.anchor = spawner.anchor;
                joint.connectedAnchor = spawner.connectedAnchor;

                // CorpseRagdoll.Start: no preprocessing, projection on so a stretched joint is pulled back
                // instead of springing, mass-ratio scaling.
                joint.enablePreprocessing = false;
                joint.enableProjection = true;
                joint.massScale = joint.connectedBody.mass / own.mass;
                joint.connectedMassScale = 1f;
            }

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
                    Part = part
                });

                AddContact(body, material, session);

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
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        // Tips the body forward as it begins to fall.
        private static void TipForward(Player player, Session session)
        {
            if (!MathUtil.TryLevel(player.Transform.forward, out Vector3 forward)) return;

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

            FixSnags(session);
            UpdateReleased(session);
            ApplyMuscles(session, step);
            ClampBodySpeeds(session);
            TraceMuscles(session);

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

            foreach (var contact in session.Contacts)
                UnityEngine.Object.Destroy(contact.gameObject);
            session.Contacts.Clear();
            session.Released.Clear();

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
            }

            foreach (var state in session.Joints)
            {
                state.Joint.enablePreprocessing = state.EnablePreprocessing;
                state.Joint.enableProjection = state.EnableProjection;
                state.Joint.massScale = state.MassScale;
                state.Joint.connectedMassScale = state.ConnectedMassScale;
            }

            // Back under the player, keeping the world pose so whatever comes next starts from what is on screen,
            // with every bone back on its parent where the rig has it (the stretch dropped). Only after a simulation: it
            // is what stretched them, and the tops it detached are what keep the pelvis out of this. A body that was only
            // animated has none, and this would put its pelvis back at the standing height, lifting it off the ground.
            if (still && session.Bodies.Count > 0) RestoreBoneOffsets(session);
            ReattachToRoot(session);

            session.Bodies.Clear();
            session.Joints.Clear();
            session.Muscles.Clear();
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

        private static void Exit(Session session, ExitPose pose = ExitPose.Blend)
        {
            var player = session.Player;
            Active.Remove(player.ProfileId);
            player.OnPlayerDeadOrUnspawn -= OnDeadOrUnspawn;

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
                    foreach (var rest in session.Rest)
                        if (rest.Bone != null) blend.Capture(rest.Bone);
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
                if (blend != null) BlendTowardAnimator(player, blend);

                RevivalDebugLog.LogDebug($"[BodyRagdoll] Exited for {player.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyRagdoll] Exit failed: {ex}");
            }
        }
    }
}
