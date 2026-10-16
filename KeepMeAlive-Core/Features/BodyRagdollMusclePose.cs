//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ BodyRagdoll: Muscle Pose ]====================
    // The pose the muscles pull toward, worked out on a copy of the skeleton.
    internal static partial class BodyRagdoll
    {
        //====================[ Muscle Pose ]====================
        // The pose the muscles pull toward and the joints take as their zero (the body itself is never put into it): the
        // first frame of the settle clip. Every bone the clip plays is turned exactly as the clip has it there (DownedPose
        // puts the same rotations on the same bones), so the body is drawn into the pose the settle starts on. There is no
        // other source: without the clip the ragdoll is not built (the caller reports why).
        private static SkeletonCopy MusclePose(Player player)
        {
            var clip = BakedClip.Settle ?? throw new InvalidOperationException("the settle clip did not load");
            var rig = ClipRig.TryBuild(player, clip) ?? throw new InvalidOperationException("the settle clip does not fit this skeleton");

            Transform[] all = player.PlayerBones.Pelvis.Original.GetComponentsInChildren<Transform>(true);
            var bind = SkeletonBind.Read(player, new HashSet<Transform>(all));
            var rotations = new Quaternion[rig.Count];
            rig.Evaluate(clip, 0f, rotations);

            var given = new Dictionary<Transform, Quaternion>();
            for (int k = 0; k < rig.Count; k++) given[rig.Bones[k]] = rig.BindFrame * rotations[k];

            var copy = new SkeletonCopy(all, bind);
            copy.Pose(given);
            return copy;
        }

        // A copy of a player's skeleton posed without moving the real one: each transform's rotation in the bind pose's own
        // space (only the rotations relative to each other are used). The bones given outright take those; every other bone
        // is as its mesh is bound, or, if no mesh is bound to it, keeps its turn relative to its parent.
        private sealed class SkeletonCopy
        {
            private readonly Transform[] _all;
            private readonly int[] _parent;
            private readonly bool[] _bound;
            private readonly Quaternion[] _bind;
            private readonly Quaternion[] _rotation;
            private readonly Dictionary<Transform, int> _index = new Dictionary<Transform, int>();

            public SkeletonCopy(Transform[] all, Dictionary<Transform, Matrix4x4> bind)
            {
                _all = all;
                int n = all.Length;
                _parent = new int[n];
                _bound = new bool[n];
                _bind = new Quaternion[n];
                _rotation = new Quaternion[n];
                for (int i = 0; i < n; i++) _index[all[i]] = i;
                for (int i = 0; i < n; i++)
                {
                    _parent[i] = all[i].parent != null && _index.TryGetValue(all[i].parent, out int p) ? p : -1;
                    if (bind.TryGetValue(all[i], out var m))
                    {
                        _bound[i] = true;
                        _bind[i] = m.rotation;
                    }
                }
            }

            // given: the bones whose rotation is given outright (bind-space world rotations).
            public void Pose(Dictionary<Transform, Quaternion> given)
            {
                for (int i = 0; i < _all.Length; i++)
                {
                    var t = _all[i];
                    int p = _parent[i];
                    if (given.TryGetValue(t, out var own)) _rotation[i] = own;
                    else if (_bound[i]) _rotation[i] = _bind[i];
                    else _rotation[i] = p >= 0 ? _rotation[p] * t.localRotation : _bind[i];
                }
            }

            public bool Has(Transform t) => t != null && _index.ContainsKey(t);
            public Quaternion Rotation(Transform t) => _rotation[_index[t]];
        }
    }
}
