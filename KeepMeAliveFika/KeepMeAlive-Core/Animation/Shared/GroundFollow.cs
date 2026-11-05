//====================[ Imports ]====================
using System.Collections.Generic;
using UnityEngine;

namespace KeepMeAlive.Animation.Shared
{
    //====================[ GroundFollow ]====================
    // Bends a pose played on flat ground (DownedPose) to the real ground, in two layers:
    //   body   - the whole pose is turned to face the way the body faces and onto the slope under it (as EFT's prone
    //            alignment does), with the pelvis kept at the clip's height above the ground under it (Follow);
    //   limbs  - each hand, foot and the head that the clip has on its (flat) ground is moved by how far the real
    //            ground under it is above or below that flat ground (a raycast), the arm or leg bent to reach it by
    //            two-bone IK and the neck turned for the head. Parts the clip holds off the ground (the hand on the
    //            chest) are left alone. The moves are smoothed over time, so nothing jitters (FitLimbs).
    // On flat ground both layers do nothing and the pose is exactly the clip.
    internal sealed class GroundFollow
    {
        //====================[ Tuning ]====================
        // The body follow, as EFT's prone MovementContext.AlignToSurface does it: the body is turned onto the slope
        // under it (down to a point under the body and one a little ahead), the surface is limited to this far from
        // flat, and the turn is smoothed at the same rate (PRONE_ALIGN_SPEED).
        private const float MaxSurfaceTilt = 30f;
        private const float AlignSpeed = 5f;
        private const float AheadProbe = 0.5f;
        private const float ProbeHeight = 1.5f;
        private const float ProbeLength = 3.5f;

        // Limb contact: a hand, foot or head the clip has within ContactHeight of its ground is on the ground and
        // follows the real one fully; above ReleaseHeight it is held off it and is left alone (m).
        private const float ContactHeight = 0.1f;
        private const float ReleaseHeight = 0.25f;
        // Most a limb is moved to meet the ground (m), and how quickly the move follows the ground (1/s).
        private const float MaxContactShift = 0.3f;
        private const float ContactFollowRate = 8f;
        // Share of the turn onto the ground's slope a hand or foot on the ground takes.
        private const float ContactTilt = 0.7f;
        // The ground under a limb is looked for from this far above it, this far down (m).
        private const float ContactProbeHeight = 0.6f;
        private const float ContactProbeLength = 1.2f;

        //====================[ State ]====================
        private sealed class Limb
        {
            // Shoulder/hip, elbow/knee, hand/foot; Mid null for the head (Root the neck, End the head).
            public Transform Root, Mid, End;
            public float Upper, Lower;
            public float Shift;
            public Vector3 Normal = Vector3.up;
        }
        private readonly List<Limb> _limbs = new List<Limb>();

        // The player's feet height (the ground is looked for from above it), whether there is ground under the body,
        // the ground's height under the body at the start, and the last one found.
        public float RootY;
        public bool HasGround { get; private set; }
        public float StartGroundY { get; private set; }
        private float _lastGroundY;
        // The smoothed slope: the way the body points along it, and its up.
        public Vector3 SurfaceDirection;
        private Vector3 _surfaceNormal = Vector3.up;

        public int LimbCount => _limbs.Count;

        public GroundFollow(float rootY)
        {
            RootY = rootY;
        }

        //====================[ Start ]====================
        // Starts from the ground under the pelvis, or the player's feet height without any. forward: the way the
        // clip's body faces.
        public void BeginAtRoot(Vector3 pelvis, Vector3 forward)
        {
            HasGround = ProbeGround(pelvis, out var under);
            StartGroundY = HasGround ? under.point.y : RootY;
            Finish(forward);
        }

        // Starts from the ground under the pelvis, which becomes the feet height; the pelvis's own height without any.
        public void BeginAtBody(Vector3 pelvis, Vector3 forward)
        {
            HasGround = ProbeGround(pelvis, out var under);
            RootY = HasGround ? under.point.y : pelvis.y;
            StartGroundY = RootY;
            Finish(forward);
        }

        private void Finish(Vector3 forward)
        {
            _lastGroundY = StartGroundY;
            SurfaceDirection = forward;
        }

        // A hand, foot or head to keep on the ground. mid: the elbow or knee (null for the head, where root is the neck).
        public void AddLimb(Transform root, Transform mid, Transform end)
        {
            _limbs.Add(new Limb
            {
                Root = root,
                Mid = mid,
                End = end,
                Upper = mid != null ? Vector3.Distance(root.position, mid.position) : 0f,
                Lower = mid != null ? Vector3.Distance(mid.position, end.position) : 0f
            });
        }

        //====================[ Limbs on the Ground ]====================
        // Moves each hand, foot and the head the clip has on its ground onto the real ground under it. plane: a point
        // on the clip's ground (under the pelvis) and its normal.
        public void FitLimbs(Vector3 planePoint, Vector3 planeNormal, float weight)
        {
            float follow = global::KeepMeAlive.Animation.Shared.MathUtil.Follow(ContactFollowRate);
            foreach (var limb in _limbs)
            {
                Vector3 end = limb.End.position;
                float height = Vector3.Dot(end - planePoint, planeNormal);
                float contact = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ContactHeight, ReleaseHeight, height));

                // How far the real ground is above (+) or below (-) the clip's at this spot.
                float wanted = 0f;
                Vector3 normal = planeNormal;
                if (contact > 0f && Physics.Raycast(end + Vector3.up * ContactProbeHeight, Vector3.down, out var hit,
                        ContactProbeLength, LayersMaskController.HighPolyWithTerrainMask))
                {
                    wanted = Mathf.Clamp(Vector3.Dot(hit.point - planePoint, planeNormal), -MaxContactShift, MaxContactShift) * contact;
                    normal = Vector3.Slerp(planeNormal, hit.normal, contact);
                }
                limb.Shift = Mathf.Lerp(limb.Shift, wanted, follow);
                limb.Normal = Vector3.Slerp(limb.Normal, normal, follow);

                float shift = limb.Shift * weight;
                if (Mathf.Abs(shift) < 1e-4f && Vector3.Angle(limb.Normal, planeNormal) < 0.1f) continue;

                Quaternion endRotation = limb.End.rotation;
                Vector3 target = end + planeNormal * shift;
                if (limb.Mid != null) SolveTwoBone(limb, target);
                else limb.Root.rotation = Quaternion.FromToRotation(end - limb.Root.position, target - limb.Root.position) * limb.Root.rotation;

                // A hand or foot keeps the clip's turn, tipped onto the slope it lies on; the head keeps its own.
                if (limb.Mid != null)
                {
                    Quaternion tilt = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(planeNormal, limb.Normal), ContactTilt * weight);
                    limb.End.rotation = tilt * endRotation;
                }
            }
        }

        // Puts the end of a limb on target by turning its upper and lower bone, the elbow or knee kept bending the way
        // the clip bends it. Out of reach, the limb straightens toward the target.
        private static void SolveTwoBone(Limb limb, Vector3 target)
        {
            Transform root = limb.Root, mid = limb.Mid, end = limb.End;
            Vector3 a = root.position;
            Vector3 dir = target - a;
            if (dir.sqrMagnitude < 1e-8f) return;
            dir.Normalize();

            Vector3 bend = Vector3.ProjectOnPlane(mid.position - a, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(end.position - mid.position, dir);
            if (bend.sqrMagnitude < 1e-8f) return;
            TwoBoneIk.Solve(root, mid, end, target, limb.Upper, limb.Lower, bend.normalized);
        }

        //====================[ Body on the Ground ]====================
        // The turn that puts the body onto the ground under `at`, and the ground's height there. Like EFT's prone
        // alignment: with a hit under the body and one ahead of it the body points along the line between them,
        // with one hit it points along the surface, and the up direction is the smoothed surface normal. forward: the
        // way the clip's body faces.
        public Quaternion Follow(Vector3 at, Vector3 forward, out float groundY)
        {
            groundY = _lastGroundY;
            if (!HasGround) return Quaternion.identity;

            bool under = ProbeGround(at, out var underHit);
            bool ahead = ProbeGround(at + forward * AheadProbe, out var aheadHit);
            if (under) groundY = _lastGroundY = underHit.point.y;

            Vector3 normal = under ? underHit.normal : ahead ? aheadHit.normal : Vector3.up;
            Vector3 direction;
            if (under && ahead) direction = (aheadHit.point - underHit.point).normalized;
            else direction = Vector3.Cross(normal, Vector3.Cross(forward, normal));
            if (direction.sqrMagnitude < 1e-6f) direction = forward;

            if (Vector3.Angle(normal, Vector3.up) >= MaxSurfaceTilt)
                normal = Quaternion.AngleAxis(MaxSurfaceTilt, Vector3.Cross(Vector3.up, normal)) * Vector3.up;

            float k = Mathf.Clamp01(Time.deltaTime * AlignSpeed);
            SurfaceDirection = Vector3.Lerp(SurfaceDirection, direction, k);
            _surfaceNormal = Vector3.Lerp(_surfaceNormal, normal, k);
            if (SurfaceDirection.sqrMagnitude < 1e-6f || _surfaceNormal.sqrMagnitude < 1e-6f) return Quaternion.identity;

            return Quaternion.LookRotation(SurfaceDirection, _surfaceNormal) * Quaternion.Inverse(Quaternion.LookRotation(forward, Vector3.up));
        }

        // The ground (terrain and high poly colliders, EFT's ground mask) straight down from above the point.
        private bool ProbeGround(Vector3 at, out RaycastHit hit)
        {
            var origin = new Vector3(at.x, RootY + ProbeHeight, at.z);
            return Physics.Raycast(origin, Vector3.down, out hit, ProbeLength, LayersMaskController.HighPolyWithTerrainMask);
        }
    }
}
