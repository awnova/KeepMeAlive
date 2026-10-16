//====================[ Imports ]====================
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KeepMeAlive.Features
{
    //====================[ StimPen ]====================
    // The stim pen in the dragger's right hand (StimJabClip). The mesh (Tools/DragPose/bake_stim_jab.py) is in the hand's
    // frame (HandFrames.FromKnuckles), so it is placed on the palm in that frame: the grip of the Blender scene, on any
    // skeleton. Not parented to the bones: Follow places it in world space, so whatever scale the skeleton carries never
    // reaches the mesh, which is in metres.
    internal sealed class StimPen
    {
        private const string MeshResource = "KeepMeAlive.Resources.stim_pen.json";
        private const string TextureResource = "KeepMeAlive.Resources.stim_pen_basecolor.png";
        private const string Schema = "keepmealive.stimpen.v1";
        // The material bundle (Tools/PropBundles/build_prop_bundles.py): the game's item material lit as a stim's is.
        private const string BundleResource = "KeepMeAlive.Resources.stim_pen.bundle";
        private const string BundleMaterialPath = "assets/keepmealive/stimpen/keepmealive_stimpen.mat";

        //====================[ Shared Assets ]====================
        private static Mesh _mesh;
        private static readonly HandProps.SharedMaterial Shared =
            new HandProps.SharedMaterial("StimPen", BundleResource, BundleMaterialPath, TextureResource, "StimPen");
        private static bool _loadFailed;

        private static bool LoadAssets()
        {
            if (_mesh != null) return true;
            if (_loadFailed) return false;
            try
            {
                var doc = EmbeddedData.ReadJson(MeshResource, Schema);
                _mesh = HandProps.LoadMesh(doc, (string)doc["name"]);
                return true;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _mesh = null;
                Plugin.LogSource.LogError($"[StimPen] Could not load the pen: {ex.Message}");
                return false;
            }
        }

        // Raid boundary: the material was made from a raid asset.
        public static void ClearMaterial() => Shared.Clear();

        //====================[ Instance ]====================
        private readonly GameObject _pen;
        private readonly Transform _palm;
        private readonly Quaternion _handFrame;
        private bool _visible;

        private StimPen(GameObject pen, Transform palm, Quaternion handFrame)
        {
            _pen = pen;
            _palm = palm;
            _handFrame = handFrame;
        }

        // A pen for a palm (hidden until shown); null when the assets or a material are missing. handFrame is the hand's
        // frame (HandFrames.FromKnuckles) relative to the palm bone.
        public static StimPen Create(Transform palm, Quaternion handFrame)
        {
            if (!LoadAssets()) return null;
            Material material;
            try
            {
                material = Shared.Get();
            }
            catch (Exception ex)
            {
                // The revive goes on without a pen.
                Plugin.LogSource.LogError($"[StimPen] Could not make the pen's material: {ex}");
                return null;
            }
            if (material == null) return null;

            return new StimPen(HandProps.CreateProp("KeepMeAlive_StimPen", _mesh, material, palm.gameObject.layer), palm, handFrame);
        }

        // Each frame after the arm is solved: the pen on its palm, in the hand's frame (the grip).
        public void Follow()
        {
            if (!_visible || _pen == null || _palm == null) return;
            _pen.transform.SetPositionAndRotation(_palm.position, _palm.rotation * _handFrame);
        }

        // How far a world point is from the pen's axis (m), for the trace; negative without a pen. The axis is the mesh's longest
        // dimension through the middle of its bounds.
        public float DistanceToAxis(Vector3 world)
        {
            if (_pen == null || _mesh == null) return -1f;
            Bounds bounds = _mesh.bounds;
            Vector3 e = bounds.extents;
            int axis = e.x >= e.y && e.x >= e.z ? 0 : (e.y >= e.z ? 1 : 2);
            Vector3 local = _pen.transform.InverseTransformPoint(world) - bounds.center;
            float along = Mathf.Clamp(local[axis], -e[axis], e[axis]);
            Vector3 onAxis = Vector3.zero;
            onAxis[axis] = along;
            return (local - onAxis).magnitude;
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible || _pen == null) return;
            _visible = visible;
            _pen.SetActive(visible);
            Follow();
        }

        public void Destroy()
        {
            if (_pen != null) Object.Destroy(_pen);
        }
    }
}
