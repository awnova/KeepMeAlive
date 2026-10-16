//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Speed ]====================
    // A downed body is not a corpse: its limbs are held by its own muscles, so they can be knocked about by a bump
    // but never whip or fly. Every tick each limb's speed (relative to the pelvis, so dragging the whole body is not
    // slowed) and spin are capped, the same held or free; the pelvis itself only has a high cap against runaway.
    internal static partial class BodyRagdoll
    {
        //====================[ Tuning ]====================
        // Fastest a limb or the chest moves relative to the pelvis (m/s) and spins (rad/s).
        private const float MaxPartSpeed = 3f;
        private const float MaxPartSpin = 10f;
        // Fastest the pelvis moves (m/s): above any drag or fall, only stops a runaway.
        private const float MaxPelvisSpeed = 12f;

        // Applied once per tick before the physics step, after the muscles have added their torque.
        private static void ClampBodySpeeds(Session session)
        {
            Rigidbody pelvis = session.Pelvis;
            if (pelvis != null)
            {
                float pelvisSpeed = pelvis.velocity.magnitude;
                session.PeakPelvisSpeed = Mathf.Max(session.PeakPelvisSpeed, pelvisSpeed);
                if (pelvisSpeed > MaxPelvisSpeed) session.SpeedClamps++;
                pelvis.velocity = Vector3.ClampMagnitude(pelvis.velocity, MaxPelvisSpeed);
            }
            Vector3 reference = pelvis != null ? pelvis.velocity : Vector3.zero;

            foreach (var state in session.Bodies)
            {
                var body = state.Body;
                if (body == null || body == pelvis) continue;

                // What the physics step left, before the cap (the trace's evidence the caps are needed and acting).
                Vector3 raw = body.velocity - reference;
                session.PeakLimbSpeed = Mathf.Max(session.PeakLimbSpeed, raw.magnitude);
                session.PeakLimbSpin = Mathf.Max(session.PeakLimbSpin, body.angularVelocity.magnitude);
                if (raw.magnitude > MaxPartSpeed) session.SpeedClamps++;
                if (body.angularVelocity.magnitude > MaxPartSpin) session.SpinClamps++;

                Vector3 relative = Vector3.ClampMagnitude(raw, MaxPartSpeed);
                body.velocity = reference + relative;
                body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, MaxPartSpin);
            }
            if (pelvis != null)
            {
                if (pelvis.angularVelocity.magnitude > MaxPartSpin) session.SpinClamps++;
                pelvis.angularVelocity = Vector3.ClampMagnitude(pelvis.angularVelocity, MaxPartSpin);
            }
        }

        // Debug logs only: what the caps did since the last trace, then starts counting afresh.
        private static string DescribeSpeedCaps(Session session)
        {
            string text = $"caps: limbs peaked {session.PeakLimbSpeed:F2} m/s vs the pelvis (cap {MaxPartSpeed:F1}) and {session.PeakLimbSpin:F2} rad/s (cap {MaxPartSpin:F1}), " +
                $"pelvis {session.PeakPelvisSpeed:F2} m/s (cap {MaxPelvisSpeed:F0}); cut speed on {session.SpeedClamps} bodies and spin on {session.SpinClamps}";
            session.SpeedClamps = session.SpinClamps = 0;
            session.PeakLimbSpeed = session.PeakLimbSpin = session.PeakPelvisSpeed = 0f;
            return text;
        }
    }
}
