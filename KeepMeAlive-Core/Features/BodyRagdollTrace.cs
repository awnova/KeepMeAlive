//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Trace ]====================
    // Debug logs only (Enable Debug Logs): what a taken-over body is doing, one place for all of it. Nothing here changes
    // anything; each trace is a line (or a few) in the log to read after a drop, a drag or a get-up.
    //   [GetUpTrace]  - the seconds after the owner took the body back
    //   [SettleTrace] - how fast the parts of a dropped body still move, against the Settle Movement Threshold
    //   [MuscleTrace] - how far each muscle is from the pose it pulls toward, and the body's shape
    internal static partial class BodyRagdoll
    {
        //====================[ Get Up Trace ]====================
        // Debug logs only: one line per tick for the seconds after the owner said the body got up, covering the roll,
        // the hand-back to EFT and what EFT's animation does with it.
        private const float GetUpTraceSeconds = 8f;

        private static void TraceAfterGetUp(Player player, bool want, Session session)
        {
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)) return;
            float since = Time.time - st.GetUpTime;
            if (st.GetUpTime <= 0f || since > GetUpTraceSeconds) return;

            var mc = player.MovementContext;
            Transform pelvis = player.PlayerBones?.Pelvis?.Original;
            string phase = session == null ? "eft" : session.Phase.ToString().ToLowerInvariant();
            Plugin.LogSource.LogInfo($"[GetUpTrace] {player.ProfileId} t={since:F2} phase={phase} state={st.State} limp={st.IsLimp} want={want} " +
                $"root={player.Transform.position}/{player.Transform.eulerAngles.y:F0} pelvis={(pelvis != null ? pelvis.position.ToString() : "-")}/{(pelvis != null ? pelvis.eulerAngles.ToString() : "-")} " +
                $"prone={mc.IsInPronePose} poseLevel={mc.PoseLevel:F2} managed={player.CurrentManagedState?.GetType().Name} " +
                $"animatorOn={player.BodyAnimatorCommon.enabled} masks={player.EnabledAnimators} playerOn={player.enabled}");
        }

        //====================[ Settle Trace ]====================
        // Every tick a dropped body is being checked for having settled (HasSettled).
        private static void TraceSettle(Session session, float free, float fastest, string fastestName, float speed,
            float fastestSpin, string spinName, float spin, bool settled)
        {
            if (!RevivalDebugLog.IsDebugLogsEnabled) return;
            Plugin.LogSource.LogInfo($"[SettleTrace] {session.Player.ProfileId} free={free:F2}s fastest={fastest:F3} m/s (limit {speed:F3}) " +
                $"fastestSpin={fastestSpin:F3} rad/s (limit {spin:F3}) settled={settled} [{fastestName.Replace(BoneNames.Prefix, "")} / {spinName.Replace(BoneNames.Prefix, "")}]");
        }

        //====================[ Trace ]====================
        // Debug logs only, a few times a second while the body is a ragdoll: how far each muscle is from the pose it pulls
        // toward (degrees), worst first, to see whether the pose is being reached.
        private static void TraceMuscles(Session session)
        {
            if (!RevivalDebugLog.IsDebugLogsEnabled || Time.time < session.NextMuscleTrace) return;
            session.NextMuscleTrace = Time.time + 0.25f;

            var errors = new List<(float Angle, Muscle Muscle)>();
            foreach (var muscle in session.Muscles)
                errors.Add((Mathf.Abs(AngleOff(muscle, muscle.TargetRelative, out _)), muscle));
            errors.Sort((a, b) => b.Angle.CompareTo(a.Angle));

            // The worst few: how far off, split into the turn about the joint's twist axis and the rest (the swing), next
            // to the joint's limits (the joint cannot go past them however hard the muscle pulls).
            var line = new System.Text.StringBuilder();
            for (int i = 0; i < errors.Count && i < 4; i++)
            {
                var muscle = errors[i].Muscle;
                var joint = muscle.Joint;
                SplitError(muscle, out float twist, out float swing);
                line.Append($"{Environment.NewLine}    {muscle.Body.name.Replace(BoneNames.Prefix, "")} off {errors[i].Angle:F0} = twist {twist:F0} + swing {swing:F0} | limits twist {joint.lowTwistLimit.limit:F0}..{joint.highTwistLimit.limit:F0} swing {joint.swing1Limit.limit:F0}/{joint.swing2Limit.limit:F0}");
            }
            Plugin.LogSource.LogInfo($"[MuscleTrace] {session.Player.ProfileId} free={Time.time - session.FreeSince:F2}s strength={KeepMeAliveSettings.MUSCLE_STRENGTH.Value:F0} muscles={errors.Count} worst:{line}{Environment.NewLine}    shape: {DescribeShape(session)}{Environment.NewLine}    {DescribeSpeedCaps(session)}");
        }

        // Debug logs only: the shape the body has taken in the world, to see whether it lies down or bends up. Each line
        // of the body (hips to chest, chest to head, hips to each knee) as degrees above the horizon (0 lies flat, 90
        // points at the sky, negative is into the ground), and whether the chest faces up (1 on its back, -1 on its front).
        private static string DescribeShape(Session session)
        {
            if (session.Pelvis == null || session.Chest == null) return "no body";
            Rigidbody head = null, leftKnee = null, rightKnee = null;
            foreach (var state in session.Bodies)
            {
                if (state.Part == EBodyPartColliderType.HeadCommon) head = state.Body;
                else if (state.Part == EBodyPartColliderType.LeftCalf) leftKnee = state.Body;
                else if (state.Part == EBodyPartColliderType.RightCalf) rightKnee = state.Body;
            }

            string Rise(Rigidbody from, Rigidbody to)
            {
                if (from == null || to == null) return "-";
                Vector3 d = (to.position - from.position).normalized;
                return $"{Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg:F0}";
            }

            string faces = "-";
            if (DragGrip.TryFront(session.LeftShoulder.position, session.RightShoulder.position, session.Pelvis.position, out Vector3 front, out _))
                faces = $"{Vector3.Dot(front, Vector3.up):F2}";
            return $"hips->chest {Rise(session.Pelvis, session.Chest)}, chest->head {Rise(session.Chest, head)}, hips->left knee {Rise(session.Pelvis, leftKnee)}, hips->right knee {Rise(session.Pelvis, rightKnee)}; chest faces up {faces}";
        }

        // The error of a muscle (the turn from where its body is to the pose it pulls toward) in its joint's own frame:
        // degrees about the joint's twist axis, and the swing that is left.
        private static void SplitError(Muscle muscle, out float twist, out float swing)
        {
            var joint = muscle.Joint;
            Vector3 x = joint.axis.normalized;
            Vector3 z = Vector3.Cross(x, joint.swingAxis).normalized;
            Vector3 y = Vector3.Cross(z, x);
            // LookRotation(z, y) turns the local axes (x, y, z) onto the joint's own, so local x is the twist axis.
            Quaternion frame = muscle.Body.rotation * Quaternion.LookRotation(z, y);
            Quaternion error = muscle.Parent.rotation * muscle.TargetRelative * Quaternion.Inverse(muscle.Body.rotation);
            Quaternion local = Quaternion.Inverse(frame) * error * frame;
            // Swing-twist about local x.
            twist = 2f * Mathf.Atan2(local.x, local.w) * Mathf.Rad2Deg;
            if (twist > 180f) twist -= 360f;
            if (twist < -180f) twist += 360f;
            Quaternion twistPart = new Quaternion(local.x, 0f, 0f, local.w);
            float length = Mathf.Sqrt(twistPart.x * twistPart.x + twistPart.w * twistPart.w);
            if (length > 1e-6f) twistPart = new Quaternion(twistPart.x / length, 0f, 0f, twistPart.w / length);
            swing = (local * Quaternion.Inverse(twistPart)).ToSignedAngleAxis(out _);
            swing = Mathf.Abs(swing);
        }
    }
}
