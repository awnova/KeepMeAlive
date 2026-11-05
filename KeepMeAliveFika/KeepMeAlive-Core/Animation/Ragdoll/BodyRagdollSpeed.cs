//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Animation.Ragdoll
{
    //====================[ BodyRagdoll: Speed ]====================
    // A downed body is not a corpse: its limbs are held by its own muscles, so they can be knocked about by a bump but never
    // whip or fly. The limits are the solver's own (Rigidbody.maxAngularVelocity / maxLinearVelocity), applied inside the physics
    // step; nothing rewrites a body's velocity between steps (that fought the solver and the muscles). Each limb's speed limit
    // rides on the pelvis's speed, so dragging the whole body is not slowed; the pelvis itself only has a high cap against runaway.
    // The same held or let go.
    internal static partial class BodyRagdoll
    {
        //====================[ Tuning ]====================
        private const float MaxPartSpeed = 3f;      // m/s, each part relative to the pelvis
        private const float MaxPartSpin = 10f;      // rad/s
        private const float MaxPelvisSpeed = 12f;   // m/s

        // Once per tick, before the step: the limits the solver applies during it. StopPhysics restores the bodies' own values.
        private static void ApplySpeedLimits(Session session)
        {
            Rigidbody pelvis = session.Pelvis;
            float pelvisSpeed = 0f;
            if (pelvis != null)
            {
                pelvisSpeed = pelvis.velocity.magnitude;
                session.PeakPelvisSpeed = Mathf.Max(session.PeakPelvisSpeed, pelvisSpeed);
                if (pelvisSpeed >= MaxPelvisSpeed - 1e-3f) session.SpeedClamps++;
                pelvis.maxLinearVelocity = MaxPelvisSpeed;
                pelvis.maxAngularVelocity = MaxPartSpin;
            }
            Vector3 pelvisVelocity = pelvis != null ? pelvis.velocity : Vector3.zero;

            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                if (body == null) continue;
                float spin = body.angularVelocity.magnitude;
                session.PeakLimbSpin = Mathf.Max(session.PeakLimbSpin, spin);
                if (spin >= MaxPartSpin - 1e-3f) session.SpinClamps++;
                body.maxAngularVelocity = MaxPartSpin;
                if (body == pelvis) continue;

                // Debug counters only (the trace's evidence of what the limits are doing).
                float relative = (body.velocity - pelvisVelocity).magnitude;
                session.PeakLimbSpeed = Mathf.Max(session.PeakLimbSpeed, relative);
                if (relative >= MaxPartSpeed - 1e-3f) session.SpeedClamps++;
                body.maxLinearVelocity = pelvisSpeed + MaxPartSpeed;
            }
        }

        // Debug logs only: what the limits met since the last trace, then starts counting afresh.
        private static string DescribeSpeedCaps(Session session)
        {
            string text = $"caps: limbs peaked {session.PeakLimbSpeed:F2} m/s vs the pelvis (cap {MaxPartSpeed:F1}) and {session.PeakLimbSpin:F2} rad/s (cap {MaxPartSpin:F1}), " +
                $"pelvis {session.PeakPelvisSpeed:F2} m/s (cap {MaxPelvisSpeed:F0}); at the speed cap {session.SpeedClamps} body-ticks, at the spin cap {session.SpinClamps}";
            session.SpeedClamps = session.SpinClamps = 0;
            session.PeakLimbSpeed = session.PeakLimbSpin = session.PeakPelvisSpeed = 0f;
            return text;
        }
    }
}
