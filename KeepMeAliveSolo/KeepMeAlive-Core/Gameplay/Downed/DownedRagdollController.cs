//====================[ Imports ]====================
using System;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedRagdollController ]====================
    // Keeps the downed local player limp until movement or a revive ends the state.
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

        //====================[ Knockback ]====================
        // Moves the downed player along the fall path so their capsule lands where the fall pose ends.
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
        private static Knockback _knockback;

        //====================[ Local Fall ]====================
        // The fall as it started, kept for the whole downed time. The local body is already going prone by the time the
        // pose could start (the animator is running its move into prone), so the pose is placed from this.
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
            _knockback = null;
        }

        //====================[ Shared ]====================
        // Whether the player should stay ragdolled, including during the death handoff.
        public static bool WantsRagdoll(RMPlayer st) =>
            st != null && st.IsLimp && (st.State == RMState.BleedingOut || (st.State == RMState.None && st.KillOverride));

        //====================[ Local Player ]====================
        public static void Begin(Player player, RMPlayer st)
        {
            st.IsLimp = true;
            st.LimpStartTime = Time.time;

            _knockback = null;
            _localFall = null;
            var fall = DownedPose.FallFor(player);
            var pelvis = PlayerUtils.GetPelvis(player);
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

            Vector3 downPelvis = pelvis != null ? pelvis.position : player.Position;
            Vector3 fromRoot = Quaternion.Inverse(DownedPose.Facing(player)) * (downPelvis - player.Position);
            st.LyingOffset = new Vector2(fromRoot.x, fromRoot.z);
            st.HasLyingOffset = pelvis != null;

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
            // Holding the self-revive key: the body lies and stabs itself, whatever keys are pressed meanwhile.
            if (st.IsSelfReviving || st.SelfReviveAwaitingAuth) return false;
            // Unconscious: the player is out and cannot pick themselves up.
            if (st.IsSilentInventoryAnimActive) return false;
            if (player.InputDirection.sqrMagnitude < MoveInputThreshold * MoveInputThreshold) return false;
            return player.MovementIdlingTime <= MoveInputMaxAge;
        }

        // Moves the player to follow the fall until the clip ends. A revive or getting up ends it early.
        private static void TickKnockback(Player player, RMPlayer st)
        {
            var knockback = _knockback;
            if (knockback == null || knockback.PlayerId != player.ProfileId) return;
            if (!st.IsLimp || st.State != RMState.BleedingOut)
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
            RevivalDebugLog.LogDebug($"[DownedRagdoll] {player.ProfileId} no longer limp ({reason})");
        }

        // Where the body getting up ends: the player's root and the yaw of their body.
        public static void GetUpTarget(Player player, out Vector3 root, out float yaw)
        {
            root = player.Position;
            yaw = player.Transform.rotation.eulerAngles.y;
        }
    }
}
