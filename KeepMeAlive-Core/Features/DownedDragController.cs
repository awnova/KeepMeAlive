//====================[ Imports ]====================
using EFT;
using EFT.InventoryLogic;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
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

        //====================[ Local State ]====================
        // Whom the local player is dragging (null when nobody).
        private static string _draggingId;
        private static float _dragStartTime;
        private static bool _crouchSettled;
        private static bool _handsSettled;
        // When the real body was first seen too far behind the ragdoll (-1 while it keeps up).
        private static float _snagSince = -1f;

        public static bool IsLocalDragging => !string.IsNullOrEmpty(_draggingId);
        public static bool IsLocalDraggingBody(string reviveeId) => IsLocalDragging && _draggingId == reviveeId;

        // The local player is picking a body up: held in place, stance and hands locked, until it is lifted.
        public static bool IsPickupLocked => TryGetLocalSession(out var session) && !session.IsHeld;

        private static bool TryGetLocalSession(out DragSession session)
        {
            session = null;
            return IsLocalDragging && DragContext.TryGetSession(_draggingId, out session);
        }

        //====================[ Eligibility ]====================
        public static bool CanStartDrag(Player dragger, Player revivee, RMPlayer reviveeState)
        {
            if (IsLocalDragging) return false;
            if (reviveeState.State != RMState.BleedingOut) return false;
            if (reviveeState.IsBeingDragged || reviveeState.IsBeingRevived || reviveeState.IsSelfReviving || reviveeState.SelfReviveAwaitingAuth) return false;
            if (RMSession.IsPlayerCritical(dragger.ProfileId)) return false;
            // Dragging is done crouched; going prone is one of the ways to let go.
            if (dragger.MovementContext.IsInPronePose) return false;
            return (dragger.Position - revivee.Position).sqrMagnitude <= StartRange * StartRange;
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
            var local = ModUtils.GetYourPlayer();
            if (local != null && draggerId == local.ProfileId && _draggingId != reviveeId)
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
            HandTrace.Log($"drag {dragger.ProfileId}: let go of {reviveeId}: {reason} | {ReleaseSnapshot(dragger, ModUtils.GetPlayerById(reviveeId))}");
            ApplyDragState(reviveeId, dragger.ProfileId, false);
            FikaBridge.SendDragStatePacket(reviveeId, dragger.ProfileId, false);

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
            if (!ModUtils.IsEmptyHanded(dragger)) dragger.SetEmptyHands(null);

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
            bool empty = ModUtils.IsEmptyHanded(dragger) || HoldsSidearm(dragger);
            if (!_handsSettled) _handsSettled = empty || !settling;
            if (_handsSettled && !empty) return "hands";

            Vector3 toBody = revivee.Position - dragger.Position;
            toBody.y = 0f;
            if (toBody.sqrMagnitude > 0.25f)
            {
                float bearing = MathUtil.YawOf(toBody);
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
            float bearing = MathUtil.YawOf(new Vector3(toBody.x, 0f, toBody.z));
            float off = Mathf.Abs(Mathf.DeltaAngle(mc.Rotation.x, bearing));
            string chest = BodyRagdoll.TryGetChest(revivee.ProfileId, out Vector3 c)
                ? $"{HandTrace.V(c)} ({HandTrace.Cm(new Vector2(c.x - dragger.Position.x, c.z - dragger.Position.z).magnitude)} from dragger)"
                : "none";
            bool held = TryGetLocalSession(out var session) && session.IsHeld;
            return $"stage {(TryGetLocalSession(out var s) ? s.Current.ToString() : "none")} held={held} for {Time.time - _dragStartTime:F2}s | " +
                $"dragger {HandTrace.V(dragger.Position)} look {mc.Rotation.x:F0} pose {mc.PoseLevel:F2} prone={mc.IsInPronePose} " +
                $"emptyHands={ModUtils.IsEmptyHanded(dragger)} sidearm={HoldsSidearm(dragger)} | " +
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
        }

        // This peer's part of a drag that ended: the hands let go.
        private static void EndSession(string reviveeId) => DragContext.End(reviveeId);

        // Hard reset for raid boundaries; nothing is broadcast because the session is gone. The drags themselves (and
        // the bodies and arms) are cleared by BodyAnimation.ClearAll.
        public static void Clear() => EndLocalDraggerSession();

        //====================[ Per-Player Tick ]====================
        // Runs for every non-AI player from Player.UpdateTick (see RevivalTickPatch).
        public static void Tick(Player player)
        {
            RMSession.TryGetPlayerState(player.ProfileId, out var st);

            if (st != null && st.IsBeingDragged) ValidateDrag(player, st);

            bool dragged = st != null && st.IsBeingDragged && st.State == RMState.BleedingOut;
            Player dragger = dragged ? ModUtils.GetPlayerById(st.CurrentDraggerId) : null;

            DragSession session = null;
            if (dragger != null)
            {
                session = DragContext.Tick(dragger, player);
            }
            else
            {
                EndSession(player.ProfileId);
            }

            if (!player.IsYourPlayer)
            {
                // A dead dragger holds nothing; the owner's release follows a round trip later. Dragged as well as
                // limp: the owner's limp flag can arrive a resync after the drag claim.
                var hold = session != null && session.Dragger.HealthController.IsAlive ? session : null;
                BodyRagdoll.Sync(player, dragged || DownedRagdollController.WantsRagdoll(st), hold);
                return;
            }

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

            if (dragger == null || !dragger.HealthController.IsAlive)
            {
                // Dragger left or died: only the body's owner announces it so exactly one release is sent.
                string draggerId = st.CurrentDraggerId;
                SetDragger(st, string.Empty);
                FikaBridge.SendDragStatePacket(revivee.ProfileId, draggerId, false);
                EndSession(revivee.ProfileId);
                return;
            }
            if (session == null || !session.IsHeld) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 root = revivee.Position;
            float yaw = mc.TransformRotation.eulerAngles.y;
            float pullOffset = MeasurePullOffset(revivee, root, yaw);
            Vector3 chest = root + MathUtil.YawForward(yaw) * pullOffset;
            Vector3 hold = session.HoldPoint;

            // Turn the body's head toward the dragger while keeping the chest in place.
            Vector3 motion = Vector3.zero;
            float targetYaw = MathUtil.YawOf(-session.Away);
            float newYaw = Mathf.MoveTowardsAngle(yaw, targetYaw, TurnSpeed * dt);
            float delta = Mathf.DeltaAngle(yaw, newYaw);
            if (Mathf.Abs(delta) > MinTurnStepDegrees && CanRotateProne(revivee, mc, delta))
            {
                RotateBody(mc, delta);
                Vector3 chestAfter = root + MathUtil.YawForward(newYaw) * pullOffset;
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
        private static bool CanRotateProne(Player player, MovementContext mc, float deltaYaw) =>
            mc.IsAbleToRotateProne(Vector3.zero, deltaYaw, Quaternion.Euler(0f, 2f * deltaYaw, 0f), player.PlayerBones.Spine1, out _);

        private static void RotateBody(MovementContext mc, float deltaYaw)
        {
            Vector2 look = mc.Rotation;
            mc.Rotation = new Vector2(look.x + deltaYaw, look.y);
            mc.ApplyRotation(Quaternion.AngleAxis(deltaYaw, Vector3.up) * mc.TransformRotation);
        }

        // Distance from the player origin to the chest bone, along the body.
        private static float MeasurePullOffset(Player player, Vector3 root, float yaw)
        {
            Transform chest = DragGrip.Chest(player);
            if (chest == null) return 0f;

            return Mathf.Max(0f, Vector3.Dot(chest.position - root, MathUtil.YawForward(yaw)));
        }

        //====================[ Dragger: Release ]====================
        private static void TickDragger(Player dragger)
        {
            if (!IsLocalDragging) return;
            DragLookLimit.Hold();

            var reviveeState = RMSession.GetPlayerState(_draggingId);
            var revivee = ModUtils.GetPlayerById(_draggingId);

            bool gone = revivee == null || !revivee.HealthController.IsAlive;
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

            if (RMSession.IsPlayerCritical(dragger.ProfileId) || !dragger.HealthController.IsAlive)
            {
                StopDrag(dragger, "dragger-downed");
                return;
            }

            string reason;
            if ((dragger.Position - revivee.Position).sqrMagnitude > MaxLeashDistance * MaxLeashDistance) reason = "leash";
            // The pickup locks the stance and hands; the rest is checked once the body is lifted.
            else if (!IsLocalHoldingBody) return;
            // The body rolled out of the dragger's hands, as their own ragdoll of it shows.
            else if (BodyRagdoll.HasRolled(revivee.ProfileId)) reason = "rolled";
            else reason = IsSnagged(revivee) ? "snagged" : BrokenStance(dragger, revivee);
            if (reason == null) return;

            StopDrag(dragger, reason, notify: true);
        }
    }
}
