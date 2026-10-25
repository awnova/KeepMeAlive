//====================[ Imports ]====================
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KeepMeAlive.Animation.Hands.DefibHands
{
    //====================[ DefibPaddles ]====================
    // The two paddles in a reviver's hands. The meshes (Build/Tools/DefibHands/bake_defib_hands.py) are in their hand's
    // frame (HandFrames.FromKnuckles), so each is placed on its palm in that frame: the grip of the Blender scene,
    // on any skeleton.
    internal sealed class DefibPaddles
    {
        private const string MeshResource = "KeepMeAlive.Resources.defib_paddles.json";
        private const string Schema = "keepmealive.defibpaddles.v1";
        // The material bundle (Build/Tools/PropBundles/build_prop_bundles.py): the game's item material with the paddles' textures.
        private const string BundleResource = "KeepMeAlive.Resources.defib_paddles.bundle";
        private const string BundleMaterialPath = "assets/keepmealive/defibpaddles/keepmealive_defibpaddles.mat";

        //====================[ Shared Assets ]====================
        // Left and right paddle meshes, and the material.
        private static Mesh[] _meshes;
        private static readonly HandProps.SharedMaterial Shared =
            new HandProps.SharedMaterial("DefibPaddles", BundleResource, BundleMaterialPath);
        private static bool _loadFailed;

        // Builds the meshes once (at raid load, so the first revive does not hitch).
        public static bool LoadAssets()
        {
            if (_meshes != null && _meshes[0] != null && _meshes[1] != null) return true;
            if (_loadFailed) return false;
            try
            {
                _meshes = LoadMeshes();
                return true;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _meshes = null;
                Plugin.LogSource.LogError($"[DefibPaddles] Could not load the paddles: {ex.Message}");
                return false;
            }
        }

        private static Mesh[] LoadMeshes()
        {
            var doc = EmbeddedData.ReadJson(MeshResource, Schema);
            var meshes = new Mesh[2];
            foreach (JObject paddle in (JArray)doc["paddles"])
                meshes[(string)paddle["hand"] == "L" ? 0 : 1] = HandProps.LoadMesh(paddle, (string)paddle["name"]);
            if (meshes[0] == null || meshes[1] == null) throw new System.IO.InvalidDataException("a paddle is missing");
            return meshes;
        }

        // Raid boundary: the material was made from a raid asset.
        public static void ClearMaterial() => Shared.Clear();

        //====================[ Instance ]====================
        private readonly GameObject[] _paddles = new GameObject[2];
        private readonly Transform[] _palms;
        private readonly Quaternion[] _handFrames;
        private bool _visible;

        private DefibPaddles(Transform[] palms, Quaternion[] handFrames)
        {
            _palms = palms;
            _handFrames = handFrames;
        }

        // A paddle for each palm (hidden until shown). Null when the assets or a material are missing. The paddles are
        // not parented to the bones: Follow places them in world space, so whatever scale the skeleton carries never
        // reaches the mesh, which is in metres.
        public static DefibPaddles Create(Transform[] palms, Quaternion[] handFrames)
        {
            if (!LoadAssets()) return null;
            var material = Shared.Get();
            if (material == null) return null;

            var paddles = new DefibPaddles(palms, handFrames);
            for (int hand = 0; hand < 2; hand++)
                paddles._paddles[hand] = HandProps.CreateProp(hand == 0 ? "KeepMeAlive_DefibPaddle_L" : "KeepMeAlive_DefibPaddle_R",
                    _meshes[hand], material, palms[hand].gameObject.layer);
            return paddles;
        }

        // Each frame after the arms are solved: each paddle on its palm, in the hand's frame (the grip).
        public void Follow()
        {
            if (!_visible) return;
            for (int hand = 0; hand < 2; hand++)
            {
                var paddle = _paddles[hand];
                var palm = _palms[hand];
                if (paddle == null || palm == null) continue;
                paddle.transform.SetPositionAndRotation(palm.position, palm.rotation * _handFrames[hand]);
            }
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            foreach (var paddle in _paddles)
                if (paddle != null) paddle.SetActive(visible);
            Follow();
        }

        public void Destroy()
        {
            for (int i = 0; i < _paddles.Length; i++)
            {
                if (_paddles[i] != null) Object.Destroy(_paddles[i]);
                _paddles[i] = null;
            }
        }
    }
}
