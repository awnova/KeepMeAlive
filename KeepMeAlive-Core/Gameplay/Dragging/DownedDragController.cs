//====================[ Imports ]====================
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Dragging
{
    //====================[ DownedDragController ]====================
    // Drag claims and releases, and each peer's part of a drag (DragSession has the pickup and the hold frame):
    //   dragger - picks the body up (locked in place until it is lifted, DragPickupLockPatches), then lets go when
    //             the drag stance breaks;
    //   revivee - their real position follows the hold frame once the body is lifted;
    //   others  - their ragdoll of the body is held by the dragger's hands (BodyRagdoll).
    // The dragger's arms are posed on every peer (DraggerArms).
    internal static class DownedDragController
    {
        //====================[ Tuning ]====================
        // How fast the revivee's body swings to line up with the pull (deg/s).
        private const float TurnSpeed = 140f;
        // Follow strength (1/s) and maximum catch-up speed (m/s) of the revivee's body toward the hold.
        private const float FollowGain = 12f;
        private const float MaxCatchUpSpeed = 14f;
        private const float FollowDeadZone = 0.03f;
        private const float MinTurnStepDegrees = 0.01f;
        // Ignore resyncs that may predate a recent local claim change (a drag here, a team-revive help in
        // RevivePacketHandlers).
        internal const float ClaimSettleTime = 2f;
        // Past this the dragger lets go (teleport, a wall snag, or the body being left behind).
        private const float MaxLeashDistance = 7f;
        // Maximum angle away from the body before releasing.
        private const float MaxTurnAwayDegrees = 50f;
        // Wait for the crouch and hands changes before checking the drag stance.
        private const float StanceSettleTimeout = 2f;
        // The game's own line between crouching and standing (Player.TogglePose).
        private const float StandingPoseLevel = 0.5f;
        private const float StartRange = 3f;
        // The revivee's real body is snagged (held back by geometry on its owner's side) while the hands carry the
        // ragdoll on: the ragdoll's chest this far (m, level) from their root, for this long (s), lets go.
        private const float SnagGap = 2.5f;
        private const float SnagSeconds = 0.75f;

        // How often the dragger's revive prompt is re-evaluated (it looks at the inventory).
        private const float StripRefreshSeconds = 0.25f;
        // How often the dragger tells the others which way their body faces (DragHeadingPacket) (s).
        private const float HeadingSendSeconds = 0.1f;

        //====================[ Local State ]====================
        // Whom the local player is dragging (null when nobody).
        private static string _draggingId;
        private static float _dragStartTime;
        private static bool _crouchSettled;
        private static bool _handsSettled;
        // When the real body was first seen too far behind the ragdoll (-1 while it keeps up).
        private static float _snagSince = -1f;
        private static float _nextStripRefresh;
        private static float _nextHeadingSend;

        public static bool IsLocalDragging => !string.IsNullOrEmpty(_draggingId);

        // The local player is picking a body up: held in place, stance and hands locked, until it is lifted.
        public static bool IsPickupLocked => TryGetLocalSession(out var session) && !session.IsHeld;

        private static bool TryGetLocalSession(out DragSession session)
        {
            session = null;
            return IsLocalDragging && DragContext.TryGetSession(_draggingId, out session);
        }

        // The world position of the downed player's visual/interactable body (ragdoll chest or posed chest/pelvis),
        //====================[ Eligibility ]====================
        // which can sit away from revivee.Position after a fall or drag release.
        public static Vector3 BodyPosition(Player revivee)
        {
            if (revivee == null) return Vector3.zero;
            if (BodyRagdoll.TryGetChest(revivee.ProfileId, out Vector3 chest)) return chest;
            Transform bone = DragGrip.Chest(revivee) ?? PlayerUtils.GetPelvis(revivee);
            return bone != null ? bone.position : revivee.Position;
        }

        public static bool IsInRange(Player viewer, Player revivee) =>
            viewer != null && revivee != null && (viewer.Position - BodyPosition(revivee)).sqrMagnitude <= StartRange * StartRange;

        public static bool CanStartDrag(Player dragger, Player revivee, RMPlayer reviveeState)
        {
            if (dragger == null || revivee == null || reviveeState == null) return false;
            if (IsLocalDragging) return false;
            if (reviveeState.State != RMState.BleedingOut) return false;
            if (reviveeState.IsBeingDragged || reviveeState.IsBeingRevived || reviveeState.IsSelfReviving || reviveeState.SelfReviveAwaitingAuth) return false;
            // Still going down: the body is picked up once it has landed, where the owner's real body has landed too.
            if (BodyRagdoll.IsFalling(revivee.ProfileId)) return false;
            if (RMSession.IsPlayerCritical(dragger.ProfileId)) return false;
            // Dragging is done crouched; going prone is one of the ways to let go.
            if (dragger.MovementContext.IsInPronePose) return false;
            return IsInRange(dragger, revivee);
        }

        //====================[ Shared State Mutation ]====================
        // Applies a drag start or release when the state allows it.
        public static bool ApplyDragState(string reviveeId, string draggerId, bool active)
        {
            if (string.IsNullOrEmpty(reviveeId) || string.IsNullOrEmpty(draggerId)) return false;
            if (active && reviveeId == draggerId) return false;

            var st = RMSession.GetPlayerState(reviveeId);

            if (!active)
            {
                if (st.CurrentDraggerId != draggerId) return false;
                SetDragger(st, string.Empty);
                return true;
            }

            if (st.State != RMState.BleedingOut) return false;
            if (st.IsBeingRevived || st.IsSelfReviving || st.SelfReviveAwaitingAuth) return false;
            // First dragger keeps the claim; the revivee's own resync settles any claims that crossed.
            if (st.IsBeingDragged && st.CurrentDraggerId != draggerId) return false;

            SetDragger(st, draggerId);
            return true;
        }

        // Applies the revivee owner's latest drag state unless a newer local change exists.
        public static void ApplyAuthoritativeDragger(string reviveeId, RMPlayer st, string draggerId)
        {
            if (st == null || string.IsNullOrEmpty(reviveeId)) return;
            draggerId ??= string.Empty;
            if (st.CurrentDraggerId == draggerId) return;
            if (Time.time - st.DragClaimTime < ClaimSettleTime) return;

            // Resend a release if the owner still thinks this player is dragging (them, not whoever is held now).
            if (PlayerUtils.IsLocalPlayer(draggerId) && _draggingId != reviveeId)
            {
                FikaBridge.SendDragStatePacket(reviveeId, draggerId, false);
                return;
            }

            RevivalDebugLog.LogNetworkTrace($"[Drag] resync: dragger of {reviveeId} '{st.CurrentDraggerId}' -> '{draggerId}'");
            SetDragger(st, draggerId);
        }

        // Every change to the claim goes through here so its time is recorded.
        private static void SetDragger(RMPlayer st, string draggerId)
        {
            st.CurrentDraggerId = draggerId ?? string.Empty;
            st.DragClaimTime = Time.time;
            // Picked up: it will be put down again, somewhere else, and said so by whoever lets go then.
            if (!string.IsNullOrEmpty(draggerId))
            {
                st.HasDropRest = false;
                st.DropDeclarerId = string.Empty;
            }
        }

        //====================[ Put Down ]====================
        // The peer that releases a lifted body reports where it settles. The release names that peer; it watches its
        // ragdoll settle, then sends the resting position (DropRestPacket). Every peer moves its copy to that position,
        // and the owner moves the real body underneath it. Both packets come from the same sender, so the rest report
        // follows the release.
        // Wait this long for the report before anchoring the body to the owner's root. This exceeds normal settling time;
        // if the reporting peer leaves or dies first, no report arrives.
        internal const float DropRestWaitSeconds = 10f;

        // The body this client let go of and is to say where it comes to rest, and until when.
        private static string _declareFor;
        private static float _declareUntil;

        private static void Release(string reviveeId, string draggerId, bool held)
        {
            var local = PlayerUtils.GetYourPlayer();
            // Only a lifted body is put down somewhere new, and only a peer with a ragdoll of it can watch it settle.
            string declarer = held && local != null && BodyRagdoll.IsRagdolled(reviveeId) ? local.ProfileId : string.Empty;
            ExpectDropRest(reviveeId, draggerId, declarer);
            _declareFor = string.IsNullOrEmpty(declarer) ? null : reviveeId;
            _declareUntil = Time.time + DropRestWaitSeconds;
            FikaBridge.SendDragStatePacket(reviveeId, draggerId, false, declarer);
        }

        // A body was let go of (every peer, from the release): whose word on where it comes to rest to wait for. A release with
        // no declarer (before the lift, a refused claim, a resent release) has nothing to wait for and changes nothing: the
        // pickup already dropped what the last drop said. The dragger and the owner can both let go of the same body at once
        // (the leash is checked on both sides); the dragger's word is the one, whichever release comes first, so every peer
        // waits for the same one.
        public static void ExpectDropRest(string reviveeId, string draggerId, string declarerId)
        {
            if (string.IsNullOrEmpty(declarerId)) return;
            var st = RMSession.GetPlayerState(reviveeId);
            if (st.IsBeingDragged) return;
            if (!string.IsNullOrEmpty(st.DropDeclarerId) && st.DropDeclarerId != declarerId && declarerId != draggerId) return;
            st.HasDropRest = false;
            st.DropDeclarerId = declarerId;
            st.DropReleaseTime = Time.time;
        }

        // This peer's ragdoll of a body has settled (BodyRagdoll): if this client let go of it, it says where it lies.
        public static void OnBodySettled(string reviveeId, Vector3 pelvis, float spineYaw, bool onBack)
        {
            if (_declareFor == null || _declareFor != reviveeId) return;
            _declareFor = null;
            var local = PlayerUtils.GetYourPlayer();
            if (local == null || Time.time > _declareUntil) return;
            if (!ApplyDropRest(reviveeId, local.ProfileId, pelvis, spineYaw, onBack)) return;
            FikaBridge.SendDropRestPacket(reviveeId, local.ProfileId, pelvis, spineYaw, onBack);
        }

        // Where a body let go of came to rest (every peer): only from the one the release named, and not once someone has
        // picked it up again.
        public static bool ApplyDropRest(string reviveeId, string declarerId, Vector3 pelvis, float spineYaw, bool onBack)
        {
            var st = RMSession.GetPlayerState(reviveeId);
            if (st.IsBeingDragged || st.State != RMState.BleedingOut) return false;
            if (string.IsNullOrEmpty(declarerId) || st.DropDeclarerId != declarerId) return false;
            if (!DownedPose.TryFacingFromSpine(spineYaw, out float facing)) return false;

            st.DropRestPelvis = pelvis;
            st.DropFacingYaw = facing;
            st.DropSpineYaw = spineYaw;
            st.DropOnBack = onBack;
            st.HasDropRest = true;

            // The owner's real body goes under it.
            var local = PlayerUtils.GetYourPlayer();
            if (local != null && local.ProfileId == reviveeId) DownedRagdollController.OnDropRest(local, st);
            return true;
        }

        //====================[ Dragger Actions ]====================
        public static void StartDrag(GamePlayerOwner owner, Player revivee)
        {
            var dragger = owner.Player;
            // The menu action can run after the revivee has left the raid.
            if (revivee == null) return;

            var st = RMSession.GetPlayerState(revivee.ProfileId);
            if (!CanStartDrag(dragger, revivee, st)) return;
            if (!ApplyDragState(revivee.ProfileId, dragger.ProfileId, true)) return;

            _draggingId = revivee.ProfileId;
            // Picked up again before it settled: no longer this client's to say where it lies. (Another body let go of
            // earlier is still said once it settles.)
            if (_declareFor == revivee.ProfileId) _declareFor = null;
            // Now, not on the next tick: the pickup lock holds from this frame.
            DragContext.Begin(dragger, revivee);
            FikaBridge.SendDragStatePacket(revivee.ProfileId, dragger.ProfileId, true);
            EnterDragStance(dragger);
            DragLookLimit.Begin(dragger);

            VFX_UI.Text(Color.cyan, PlayerFacingMessages.Interaction.DragHint);
            RevivalDebugLog.LogDebug($"[Drag] {dragger.ProfileId} started dragging {revivee.ProfileId}");
        }

        // Dragger-initiated release (menu, stance, distance, own downing). Broadcasts to everyone; notify tells the dragger.
        public static void StopDrag(Player dragger, string reason, bool notify = false)
        {
            if (!IsLocalDragging) return;

            string reviveeId = _draggingId;
            HandTrace.Log($"drag {dragger.ProfileId}: let go of {reviveeId}: {reason} | {ReleaseSnapshot(dragger, PlayerUtils.GetPlayerById(reviveeId))}");
            bool held = IsLocalHoldingBody;
            ApplyDragState(reviveeId, dragger.ProfileId, false);
            Release(reviveeId, dragger.ProfileId, held);

            EndLocalDraggerSession();
            EndSession(reviveeId);
            RevivalDebugLog.LogDebug($"[Drag] released {reviveeId} ({reason})");
            if (notify) VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
        }

        // Crouch and stow the weapon. Once the body is lifted, undoing either lets go.
        private static void EnterDragStance(Player dragger)
        {
            var mc = dragger.MovementContext;
            if (mc.PoseLevel > 0f) dragger.CurrentManagedState.ChangePose(-mc.PoseLevel);
            if (!PlayerUtils.IsEmptyHanded(dragger)) dragger.SetEmptyHands(null);

            _dragStartTime = Time.time;
            _snagSince = -1f;
            _crouchSettled = false;
            _handsSettled = false;
        }

        // The weapon in hand is the one in the holster slot.
        private static bool HoldsSidearm(Player player)
        {
            var weapon = player.HandsController?.Item as Weapon;
            if (weapon == null) return false;
            var holster = player.Equipment?.GetSlot(EquipmentSlot.Holster);
            return holster != null && holster.ContainedItem == weapon;
        }

        // The local player has lifted the body and still drags it.
        public static bool IsLocalHoldingBody => TryGetLocalSession(out var session) && session.IsHeld;

        // Returns the release reason when the drag stance is broken.
        private static string BrokenStance(Player dragger, Player revivee)
        {
            var mc = dragger.MovementContext;
            bool settling = Time.time - _dragStartTime < StanceSettleTimeout;

            // Stood up (sprinting does that too) or went prone.
            bool crouched = !mc.IsInPronePose && mc.PoseLevel < StandingPoseLevel;
            if (!_crouchSettled) _crouchSettled = crouched || !settling;
            if (_crouchSettled && !crouched) return "stance";

            // Took anything but the sidearm into their hands (it needs only the right; the left keeps the body):
            // weapon, grenade, knife, meds.
            bool empty = PlayerUtils.IsEmptyHanded(dragger) || HoldsSidearm(dragger);
            if (!_handsSettled) _handsSettled = empty || !settling;
            if (_handsSettled && !empty) return "hands";

            Vector3 toBody = BodyPosition(revivee) - dragger.Position;
            toBody.y = 0f;
            if (toBody.sqrMagnitude > 0.25f)
            {
                float bearing = global::KeepMeAlive.Animation.Shared.MathUtil.YawOf(toBody);
                float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
                if (Mathf.Min(off, 180f - off) > MaxTurnAwayDegrees) return "turned";
            }

            return null;
        }

        // Every input the release checks read, as they are now, for the trace: so a let-go can be told from the log (the [Drag] released
        // line is debug level, which the log file does not keep).
        private static string ReleaseSnapshot(Player dragger, Player revivee)
        {
            if (dragger == null || revivee == null) return "dragger or revivee missing";
            var mc = dragger.MovementContext;
            Vector3 toBody = revivee.Position - dragger.Position;
            float flat = new Vector2(toBody.x, toBody.z).magnitude;
            float bearing = global::KeepMeAlive.Animation.Shared.MathUtil.YawOf(new Vector3(toBody.x, 0f, toBody.z));
            float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
            string chest = BodyRagdoll.TryGetChest(revivee.ProfileId, out Vector3 c)
                ? $"{HandTrace.V(c)} ({HandTrace.Cm(new Vector2(c.x - dragger.Position.x, c.z - dragger.Position.z).magnitude)} from dragger)"
                : "none";
            bool held = TryGetLocalSession(out var session) && session.IsHeld;
            return $"stage {(TryGetLocalSession(out var s) ? s.Current.ToString() : "none")} held={held} for {Time.time - _dragStartTime:F2}s | " +
                $"dragger {HandTrace.V(dragger.Position)} look {mc.Rotation.x:F0} pose {mc.PoseLevel:F2} prone={mc.IsInPronePose} " +
                $"emptyHands={PlayerUtils.IsEmptyHanded(dragger)} sidearm={HoldsSidearm(dragger)} | " +
                $"revivee root {HandTrace.V(revivee.Position)} {HandTrace.Cm(flat)} away bearing {bearing:F0} off look {off:F0} (turned check {(flat * flat > 0.25f ? "on" : "off")}, " +
                $"limit {MaxTurnAwayDegrees:F0}, counts {Mathf.Min(off, 180f - off):F0}) | ragdoll chest {chest} | rolled={BodyRagdoll.HasRolled(revivee.ProfileId)}";
        }

        // The revivee's real body (moved by its owner toward the hold) has not kept up with the ragdoll the hands carry.
        private static bool IsSnagged(Player revivee)
        {
            if (!BodyRagdoll.TryGetChest(revivee.ProfileId, out Vector3 chest)) return false;
            Vector3 gap = chest - revivee.Position;
            gap.y = 0f;
            if (gap.sqrMagnitude < SnagGap * SnagGap)
            {
                _snagSince = -1f;
                return false;
            }
            if (_snagSince < 0f) _snagSince = Time.time;
            return Time.time - _snagSince >= SnagSeconds;
        }

        // Local cleanup only; the shared state is cleared by the caller.
        private static void EndLocalDraggerSession()
        {
            _draggingId = null;
            DragLookLimit.Restore();
            Hud.ClearDraggerStrip();
        }

        // This peer's part of a drag that ended: the hands let go.
        private static void EndSession(string reviveeId) => DragContext.End(reviveeId);

        // Hard reset for raid boundaries; nothing is broadcast because the session is gone. The drags themselves (and
        // the bodies and arms) are cleared by BodyAnimation.ClearAll.
        public static void Clear()
        {
            _pullOffset = 0f;
            _declareFor = null;
            _traceHasRoot = false;
            _traceRoot = Vector3.zero;
            EndLocalDraggerSession();
        }

        // Owner only: logs every frame the real root moves further than a step could explain, with what could have moved it.
        private static Vector3 _traceRoot;
        private static bool _traceHasRoot;
        private static void TraceOwnRoot(Player player, RMPlayer st)
        {
            Vector3 root = player.Position;
            if (_traceHasRoot)
            {
                Vector3 d = root - _traceRoot;
                d.y = 0f;
                if (d.magnitude > 0.5f)
                    HandTrace.Log($"[RootTrace] jump {d.magnitude:F2} m {HandTrace.V(_traceRoot)} -> {HandTrace.V(root)} state={st?.State} dragged={st?.IsBeingDragged} limp={st?.IsLimp} " +
                        $"dropRest={st?.HasDropRest} ragdolled={BodyRagdoll.IsRagdolled(player.ProfileId)} transform={HandTrace.V(player.Transform.Original.position)}");
            }
            _traceRoot = root;
            _traceHasRoot = true;
        }

        //====================[ Per-Player Tick ]====================
        // Runs for every non-AI player from Player.UpdateTick (see RevivalTickPatch).
        public static void Tick(Player player)
        {
            RMSession.TryGetPlayerState(player.ProfileId, out var st);
            if (player.IsYourPlayer) TraceOwnRoot(player, st);

            if (st != null && st.IsBeingDragged) ValidateDrag(player, st);

            bool dragged = st != null && st.IsBeingDragged && st.State == RMState.BleedingOut;
            Player dragger = dragged ? PlayerUtils.GetPlayerById(st.CurrentDraggerId) : null;

            DragSession session = null;
            if (dragger != null)
            {
                session = DragContext.Tick(dragger, player);
            }
            else
            {
                EndSession(player.ProfileId);
            }

            // The ragdoll of the body: every peer's copy of it, the owner's own included. A dead dragger holds nothing;
            // the owner's release follows a round trip later. Dragged as well as limp: the owner's limp flag can arrive a
            // resync after the drag claim.
            var hold = session != null && PlayerUtils.IsAlive(session.Dragger) ? session : null;
            if (player.IsYourPlayer && st != null && st.IsCritical && player.MovementContext != null) CachePullOffset(player);
            BodyRagdoll.Sync(player, dragged || DownedRagdollController.WantsRagdoll(st), hold);
            if (!player.IsYourPlayer) return;

            if (dragged) TickDragged(player, st, dragger, session);
            TickDragger(player);
        }

        // Drops a drag that can no longer hold. Every peer reaches the same conclusion from replicated state.
        private static void ValidateDrag(Player player, RMPlayer st)
        {
            // The dragger's own revive keeps the drag until it is done (the hand jabs the stim while the body is held); anyone
            // else's ends it.
            bool ended = DragEnded(st);
            bool selfReviving = st.IsSelfReviving || st.SelfReviveAwaitingAuth;
            if (!ended && !selfReviving) return;

            string draggerId = st.CurrentDraggerId;
            SetDragger(st, string.Empty);

            // Only the owner of the revivee announces a self-revive release; everything else is
            // already derivable by every peer.
            if (selfReviving && player.IsYourPlayer)
                FikaBridge.SendDragStatePacket(player.ProfileId, draggerId, false);
        }

        // The body is no longer down, or someone other than the dragger is reviving it: the drag cannot go on.
        private static bool DragEnded(RMPlayer st) => st.State != RMState.BleedingOut || RevivedByOther(st);

        // A revive claim by someone other than the dragger.
        private static bool RevivedByOther(RMPlayer st) => st.IsBeingRevived && st.CurrentReviverId != st.CurrentDraggerId;

        //====================[ Revivee: Move Real Position ]====================
        // Once the body is lifted, the revivee's chest follows the hold point, the body turning so its head points
        // at the dragger, as every ragdoll of it does. Still until then: the pickup happens where it lies.
        private static void TickDragged(Player revivee, RMPlayer st, Player dragger, DragSession session)
        {
            var mc = revivee.MovementContext;

            if (!PlayerUtils.IsAlive(dragger))
            {
                // Dragger left or died: only the body's owner announces it so exactly one release is sent, and puts the
                // body down from its own ragdoll of it (the dragger can no longer).
                string draggerId = st.CurrentDraggerId;
                bool held = session != null && session.IsHeld;
                SetDragger(st, string.Empty);
                Release(revivee.ProfileId, draggerId, held);
                EndSession(revivee.ProfileId);
                return;
            }

            // The dragger's client enforces this leash too, but its release packet can arrive after the observed
            // dragger has already jumped on the revivee's client. Never chase a stale/teleported network target
            // across the map while waiting for that packet.
            if ((dragger.Position - revivee.Position).sqrMagnitude > MaxLeashDistance * MaxLeashDistance)
            {
                string draggerId = st.CurrentDraggerId;
                HandTrace.Log($"drag {draggerId}: revivee-side leash release | {ReleaseSnapshot(dragger, revivee)}");
                bool held = session != null && session.IsHeld;
                SetDragger(st, string.Empty);
                Release(revivee.ProfileId, draggerId, held);
                EndSession(revivee.ProfileId);
                return;
            }
            if (session == null || !session.IsHeld) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 root = revivee.Position;
            float yaw = mc.TransformRotation.eulerAngles.y;
            float pullOffset = MeasurePullOffset(revivee, root, yaw);
            Vector3 chest = root + global::KeepMeAlive.Animation.Shared.MathUtil.YawForward(yaw) * pullOffset;
            Vector3 hold = session.HoldPoint;

            // Turn the body's head toward the dragger while keeping the chest in place.
            Vector3 motion = Vector3.zero;
            float targetYaw = global::KeepMeAlive.Animation.Shared.MathUtil.YawOf(-session.Away);
            float newYaw = Mathf.MoveTowardsAngle(yaw, targetYaw, TurnSpeed * dt);
            float delta = Mathf.DeltaAngle(yaw, newYaw);
            if (Mathf.Abs(delta) > MinTurnStepDegrees && CanRotateProne(revivee, mc, delta))
            {
                RotateBody(mc, delta);
                Vector3 chestAfter = root + global::KeepMeAlive.Animation.Shared.MathUtil.YawForward(newYaw) * pullOffset;
                motion = chest - chestAfter;
                motion.y = 0f;
            }

            // Move the chest toward the hold point with proportional catch-up.
            Vector3 gap = hold - chest;
            gap.y = 0f;
            float distance = gap.magnitude;
            if (distance > FollowDeadZone)
            {
                float step = Mathf.Min(distance * Mathf.Min(1f, FollowGain * dt), MaxCatchUpSpeed * dt);
                motion += gap / distance * step;
            }

            if (motion.sqrMagnitude < 1e-8f) return;
            ForcedMotion.Apply(mc, motion, dt);
        }

        // The game's own check for turning while prone (would the body swing into a wall or off a ledge).
        // While the body is a ragdoll its spine is out from under the root, so the root itself is the pivot.
        private static bool CanRotateProne(Player player, MovementContext mc, float deltaYaw) =>
            PlayerUtils.CanRotateProne(mc, deltaYaw,
                BodyRagdoll.IsPhysical(player.ProfileId) ? player.Transform.Original : PlayerUtils.GetSpine1(player));

        private static void RotateBody(MovementContext mc, float deltaYaw)
        {
            Vector2 look = mc.Rotation;
            mc.Rotation = new Vector2(look.x + deltaYaw, look.y);
            PlayerUtils.RotateBodyYaw(mc, deltaYaw);
        }

        // Distance from the player origin to the chest bone, along the body.
        private static float MeasurePullOffset(Player player, Vector3 root, float yaw)
        {
            // A ragdoll chest is held at the hold point itself, and a posed body lies where the clip puts it: the root follows by
            // the offset EFT's own prone body had (CachePullOffset).
            if (BodyRagdoll.HasSession(player.ProfileId)) return _pullOffset;

            Transform chest = DragGrip.Chest(player);
            if (chest == null) return 0f;

            return Mathf.Max(0f, Vector3.Dot(chest.position - root, global::KeepMeAlive.Animation.Shared.MathUtil.YawForward(yaw)));
        }

        // The local body's chest offset from its root, as it was the last time the body was not a ragdoll.
        private static float _pullOffset;

        // Longest the root can sit behind the chest (m): a prone body's chest is under a metre from its root.
        private const float MaxPullOffset = 1f;

        // The offset comes from EFT's own prone body, which is attached to the root: the animation under the pose while one plays
        // (the pose body lies where the clip puts it), the live bones when the body is not taken over, and what was last seen
        // while it is a ragdoll (whose chest is held at the hold itself).
        private static void CachePullOffset(Player player)
        {
            string id = player.ProfileId;
            if (BodyRagdoll.TryGetAnimatedChestAhead(id, out float animated))
                _pullOffset = Mathf.Clamp(animated, 0f, MaxPullOffset);
            else if (!BodyRagdoll.HasSession(id))
                _pullOffset = Mathf.Clamp(MeasurePullOffset(player, player.Position, player.MovementContext.TransformRotation.eulerAngles.y), 0f, MaxPullOffset);
        }

        //====================[ Dragger: Revive ]====================
        // The body in the dragger's hands has no context menu; the interact key held revives it (stim jab, DraggerArms).
        // The press is consumed by DraggerReviveInputPatch; the release ends the hold as it does any other.
        // Fastest the dragger may still be moving when the revive hold starts (m/s).
        private const float MaxReviveStartSpeed = 0.1f;

        public static bool TryBeginReviveHold(GamePlayerOwner owner)
        {
            if (!IsLocalHoldingBody || RMSession.IsPlayerCritical(owner.Player.ProfileId)) return false;

            // The game is back in Idle while the dragger is still sliding to a stop; the sudden freeze of the hold would swing the
            // body in their hands past the roll limit and drop it. A hold already running ignores the press (OnRevive).
            if (!CrouchHoldState.IsActive(owner.Player) && owner.Player.Velocity.magnitude > MaxReviveStartSpeed)
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.CannotReviveWhileMoving);
                return true;
            }

            if (BodyInteractableRuntime.TryGet(_draggingId, out var body)) body.RequestRevive(owner);
            return true;
        }

        // "Hold [interact key] Revive" on the lower strip while the body is held.
        private static void UpdateReviveStrip(Player dragger)
        {
            if (Time.time < _nextStripRefresh) return;
            _nextStripRefresh = Time.time + StripRefreshSeconds;

            if (!IsLocalHoldingBody || CrouchHoldState.IsActive(dragger) || !RevivePolicy.IsEnabled(ReviveSource.Team))
            {
                Hud.ClearDraggerStrip();
                return;
            }

            var st = RMSession.GetPlayerState(_draggingId);
            int cost = RevivePolicy.LivesCost(ReviveSource.Team);
            var state = !RevivePolicy.HasRequiredItem(ReviveSource.Team, dragger) ? Hud.StripState.NeedsStim
                : st.LivesRemaining < cost ? Hud.StripState.NoLives
                : Hud.StripState.Available;

            // The cost is in the downed teammate's lives.
            Hud.SetDraggerStrip(state, cost, Hud.InteractKeyName());
        }

        //====================[ Dragger: Release ]====================
        private static void TickDragger(Player dragger)
        {
            if (!IsLocalDragging) return;
            DragLookLimit.Hold();
            // The hold frame faces the way the dragger's body does; only this client knows that yaw (Fika sends the look).
            if (Time.time >= _nextHeadingSend)
            {
                _nextHeadingSend = Time.time + HeadingSendSeconds;
                FikaBridge.SendDragHeadingPacket(dragger.ProfileId, dragger.Transform.rotation.eulerAngles.y);
            }
            UpdateReviveStrip(dragger);

            var reviveeState = RMSession.GetPlayerState(_draggingId);
            var revivee = PlayerUtils.GetPlayerById(_draggingId);

            bool gone = !PlayerUtils.IsAlive(revivee);
            if (gone || reviveeState.CurrentDraggerId != dragger.ProfileId
                || DragEnded(reviveeState))
            {
                string reviveeId = _draggingId;
                HandTrace.Log($"drag {dragger.ProfileId}: drag ended for {reviveeId}: gone={gone} claim='{reviveeState.CurrentDraggerId}' state={reviveeState.State} " +
                    $"beingRevived={reviveeState.IsBeingRevived} reviver='{reviveeState.CurrentReviverId}' | {ReleaseSnapshot(dragger, revivee)}");
                if (reviveeState.CurrentDraggerId == dragger.ProfileId) SetDragger(reviveeState, string.Empty);
                EndLocalDraggerSession();
                EndSession(reviveeId);
                // Lost the claim or someone else took over; a revive this player started needs no notice.
                if (reviveeState.CurrentReviverId != dragger.ProfileId)
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.DragReleased);
                return;
            }

            if (!PlayerUtils.IsAliveAndStanding(dragger))
            {
                StopDrag(dragger, "dragger-downed");
                return;
            }

            string reason;
            if ((dragger.Position - BodyPosition(revivee)).sqrMagnitude > MaxLeashDistance * MaxLeashDistance) reason = "leash";
            // The pickup locks the stance and hands; the rest is checked once the body is lifted.
            else if (!IsLocalHoldingBody) return;
            // The body rolled out of the dragger's hands, as their own ragdoll of it shows.
            else if (BodyRagdoll.HasRolled(revivee.ProfileId)) reason = "rolled";
            else reason = IsSnagged(revivee) ? "snagged" : BrokenStance(dragger, revivee);
            if (reason == null) return;

            // Standing, drawing or turning away is the dragger's own doing (the drag hint says so); only a drop they
            // didn't choose is told.
            bool notify = reason == "leash" || reason == "rolled" || reason == "snagged";
            StopDrag(dragger, reason, notify);
        }
    }
}
