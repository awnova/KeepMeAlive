//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedRagdollController ]====================
    // Keeps a downed player limp until movement or a revive ends the state.
    internal static class DownedRagdollController
    {
        //====================[ Tuning ]====================
        // Minimum time before movement can end the limp state.
        private const float MinLimpSeconds = 1.5f;
        // Movement input below this is stick noise, not a deliberate move.
        private const float MoveInputThreshold = 0.1f;
        // Ignore stale input when the game skips movement-axis updates.
        private const float MoveInputMaxAge = 0.2f;
        // Fastest the knockback moves the player to keep up with the fall (m/s); the clip peaks well below it.
        private const float MaxKnockbackSpeed = 4f;
        // How long an observer aims the body getting up at where the owner said they got up (s); longer than the roll.
        private const float GetUpAnchorSeconds = 5f;

        //====================[ Knockback ]====================
        // Observers play a fall that carries the body back (DownedPose). The downed player is moved along the same
        // path here, so their replicated position ends up where every observer's body lands.
        private sealed class Knockback
        {
            public string PlayerId;
            public BakedClip Clip;
            public float Start;
            public Vector3 Origin;
            public Quaternion Facing;
            public float Scale;

            // Where the fall has carried the player t seconds in.
            public Vector3 At(float t) => Origin + DownedPose.Travel(Clip, t, Facing, Scale);
        }
        // The fall the local player is following, or null.
        private static Knockback _knockback;

        //====================[ Put Down ]====================
        // After a drag, whoever let go of the body declared where it is to lie (DownedDragController.ApplyDropRest). Every
        // peer's body eases onto that place (DownedPose.AnchorToRoot), and the owner's real body goes under it (a few strides
        // at most), so the revive prompt and every later get-up start from it too.
        private const float MaxPutDownMove = 3f;
        private const float PutDownMoveSeconds = 2f;
        private const float PutDownMoveDone = 0.03f;

        private sealed class PutDownMove
        {
            public string PlayerId;
            public Vector3 Target;
            public float Until;
        }
        private static PutDownMove _putDownMove;

        // The owner's body was put down (owner only): the real body starts toward where it is to lie, right away.
        public static void OnDropRest(Player player, RMPlayer st)
        {
            _putDownMove = null;
            if (!st.HasLyingOffset || !st.HasDropRest || st.State != RMState.BleedingOut || st.IsBeingDragged) return;

            // On its back the pose lies with its pelvis at the root plus the lying offset, as every peer's anchor has it. On
            // its front EFT's own prone body is drawn on the root, its pelvis about on it.
            Vector3 fromRoot = st.DropOnBack
                ? Quaternion.Euler(0f, st.DropFacingYaw, 0f) * new Vector3(st.LyingOffset.x, 0f, st.LyingOffset.y)
                : Vector3.zero;
            Vector3 target = st.DropRestPelvis - fromRoot;
            Vector3 gap = target - player.Position;
            gap.y = 0f;
            // Never chase a body across the map (a teleport, or a ragdoll that was flung): the root stays, the body eases to it.
            if (gap.magnitude > PutDownMoveDone && gap.magnitude <= MaxPutDownMove)
                _putDownMove = new PutDownMove { PlayerId = player.ProfileId, Target = target, Until = Time.time + PutDownMoveSeconds };
        }

        // Moves the real body under the declared rest at the knockback's pace, through the game's own collision.
        private static void TickPutDownMove(Player player, RMPlayer st)
        {
            var move = _putDownMove;
            if (move == null) return;
            // Taken away (picked up again, got up, revived), there, or out of time (stopped by something in the way).
            if (move.PlayerId != player.ProfileId || st.IsBeingDragged || !st.IsLimp || st.State != RMState.BleedingOut
                || Time.time > move.Until)
            {
                _putDownMove = null;
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 gap = move.Target - player.Position;
            gap.y = 0f;
            if (gap.sqrMagnitude < PutDownMoveDone * PutDownMoveDone)
            {
                _putDownMove = null;
                return;
            }

            try
            {
                ForcedMotion.Apply(player.MovementContext, Vector3.ClampMagnitude(gap, MaxKnockbackSpeed * dt), dt);
            }
            catch (Exception ex)
            {
                _putDownMove = null;
                Plugin.LogSource.LogWarning($"[DownedRagdoll] Put-down move failed: {ex.Message}");
            }
        }

        //====================[ Local Fall ]====================
        // The fall as it started, kept for the whole downed time. The local body is already going prone by the time the
        // pose could start (the animator is running its move into prone), so the pose cannot be placed from the body
        // on screen as an observer's is: it is placed from this.
        internal sealed class LocalFall
        {
            public string PlayerId;
            public BakedClip Clip;
            public float Start;
            public Vector3 Pelvis;
            public Quaternion Facing;
        }
        private static LocalFall _localFall;

        // The recorded fall of this player, or null (none recorded: they went down already lying).
        internal static LocalFall LocalFallOf(Player player) =>
            _localFall != null && _localFall.PlayerId == player.ProfileId ? _localFall : null;

        internal static void ClearLocalFall()
        {
            _localFall = null;
            _putDownMove = null;
        }

        //====================[ Shared ]====================
        // Whether the player should stay ragdolled, including during the death handoff.
        public static bool WantsRagdoll(RMPlayer st) =>
            st != null && st.IsLimp && (st.State == RMState.BleedingOut || (st.State == RMState.None && st.KillOverride));

        //====================[ Owner ]====================
        public static void Begin(Player player, RMPlayer st)
        {
            st.IsLimp = true;
            st.LimpStartTime = Time.time;

            // Same test the observers make before playing the fall.
            _knockback = null;
            _localFall = null;
            var fall = DownedPose.FallFor(player);
            var pelvis = player.PlayerBones?.Pelvis?.Original;
            if (fall != null && pelvis != null)
            {
                _localFall = new LocalFall
                {
                    PlayerId = player.ProfileId,
                    Clip = fall,
                    Start = Time.time,
                    Pelvis = pelvis.position,
                    Facing = DownedPose.Facing(player)
                };
            }

            // What every other peer needs to play the same fall from the same place (BleedingOutPacket), and where the
            // pelvis lies from the root, which the body keeps from here on (DownedPose eases onto the root plus it).
            st.DownFallKind = _localFall == null ? 0 : fall == BakedClip.CrouchFall ? 2 : 1;
            st.DownPelvis = pelvis != null ? pelvis.position : player.Position;
            st.DownYaw = player.Transform.rotation.eulerAngles.y;
            st.DownFallTime = Time.time;
            Vector3 fromRoot = Quaternion.Inverse(DownedPose.Facing(player)) * (st.DownPelvis - player.Position);
            st.LyingOffset = new Vector2(fromRoot.x, fromRoot.z);
            st.HasLyingOffset = pelvis != null;
            st.HasDropRest = false;
            st.DropDeclarerId = string.Empty;
            if (fall != null && DownedPose.TryGetScale(player, out float scale))
            {
                _knockback = new Knockback
                {
                    PlayerId = player.ProfileId,
                    Clip = fall,
                    Start = Time.time,
                    Origin = player.Position,
                    Facing = DownedPose.Facing(player),
                    Scale = scale
                };
            }
        }

        // Per tick for the local player while critical.
        public static void TickLocal(Player player, RMPlayer st)
        {
            TickKnockback(player, st);
            TickPutDownMove(player, st);

            // Keep the revivee limp while dragged and restart the minimum time on release.
            if (st.IsBeingDragged && st.State == RMState.BleedingOut)
            {
                if (!st.IsLimp)
                {
                    st.IsLimp = true;
                    st.ResyncCooldown = -1f;
                }
                st.LimpStartTime = Time.time;
                return;
            }

            if (!st.IsLimp) return;

            // A revive (or anything else that ends the bleed-out) gets the body up.
            if (st.State != RMState.BleedingOut)
            {
                End(player, st, $"state {st.State}");
                return;
            }

            if (!DeliberateMove(player, st)) return;

            End(player, st, "moved");
        }

        // The player is meaning to move: past the minimum limp time, conscious, and the movement input is current.
        public static bool DeliberateMove(Player player, RMPlayer st)
        {
            if (st.IsLimp && Time.time - st.LimpStartTime < MinLimpSeconds) return false;
            // Unconscious: the player is out and cannot pick themselves up.
            if (st.IsSilentInventoryAnimActive) return false;
            if (player.InputDirection.sqrMagnitude < MoveInputThreshold * MoveInputThreshold) return false;
            return player.MovementIdlingTime <= MoveInputMaxAge;
        }

        // Moves the player to follow the fall until the clip ends. A drag, a revive or getting up ends it early.
        private static void TickKnockback(Player player, RMPlayer st)
        {
            var knockback = _knockback;
            if (knockback == null || knockback.PlayerId != player.ProfileId) return;
            if (st.IsBeingDragged || !st.IsLimp || st.State != RMState.BleedingOut)
            {
                _knockback = null;
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            float t = Time.time - knockback.Start;
            if (t >= knockback.Clip.Duration) _knockback = null;

            Vector3 motion = knockback.At(t) - player.Position;
            motion.y = 0f;
            motion = Vector3.ClampMagnitude(motion, MaxKnockbackSpeed * dt);
            if (motion.sqrMagnitude < 1e-8f) return;

            try
            {
                ForcedMotion.Apply(player.MovementContext, motion, dt);
            }
            catch (Exception ex)
            {
                _knockback = null;
                Plugin.LogSource.LogWarning($"[DownedRagdoll] Knockback failed: {ex.Message}");
            }
        }

        private static void End(Player player, RMPlayer st, string reason)
        {
            st.IsLimp = false;
            _localFall = null;
            // Taking the body back alive (not dying): observers roll it onto where the player is now.
            if (st.State is RMState.BleedingOut or RMState.Reviving or RMState.Revived)
                FikaBridge.SendGetUpPacket(player.ProfileId, player.Transform.rotation.eulerAngles.y);
            // Sends the resync this tick so observers get the body up straight away.
            st.ResyncCooldown = -1f;
            RevivalDebugLog.LogDebug($"[DownedRagdoll] {player.ProfileId} no longer limp ({reason})");
        }

        //====================[ Observers ]====================
        // The owner took the body back: it gets up now, without waiting for their resync, aimed at the yaw they said
        // their body had.
        public static void OnRemoteGetUp(string playerId, float yaw)
        {
            var st = RMSession.GetPlayerState(playerId);
            st.IsLimp = false;

            if (ModUtils.GetPlayerById(playerId) == null) return;
            st.GetUpYaw = yaw;
            st.GetUpTime = Time.time;
        }

        // Where the body getting up ends: the player's root and the yaw of their body. The root is the replicated
        // player as this peer sees it: that is where EFT draws the body once it has it back, and every observer sees it
        // with its own delay, so the owner's word of where they got up (a different clock) would leave the body short
        // of or past it. The yaw is the owner's word, when it came: an observer's own root yaw is no use, it stopped
        // turning when the body was ragdolled (EFT turns it only from the body animator, which BodyRagdoll turns off,
        // and Fika never sends it), and without the packet (not come yet, or from an earlier get-up) the look.
        public static void GetUpTarget(Player player, out Vector3 root, out float yaw)
        {
            root = player.Position;
            // The local body's yaw is the root's (what the get-up packet tells observers), not the look.
            yaw = player.IsYourPlayer ? player.Transform.rotation.eulerAngles.y : player.Rotation.x;
            if (player.IsYourPlayer) return;
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st) || Time.time - st.GetUpTime > GetUpAnchorSeconds) return;
            yaw = st.GetUpYaw;
        }
    }
}
