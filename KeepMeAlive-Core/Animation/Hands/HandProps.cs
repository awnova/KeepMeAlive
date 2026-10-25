//====================[ Imports ]====================
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ HandProps ]====================
    // What the props the mod puts in a player's hands (the defibrillator paddles, the stim pen) are made of, in one place: a
    // mesh from baked JSON (Build/Tools/DefibHands/bake_defib_hands.py, Build/Tools/DragPose/bake_stim_jab.py), a material and its
    // textures from an embedded bundle (Build/Tools/PropBundles/build_prop_bundles.py) and a renderer set up the way the game sets up
    // its own items.
    internal static class HandProps
    {
        //====================[ Mesh ]====================
        // A mesh from "vertices", "normals", "uv" and "triangles" arrays (the hand's frame, metres).
        public static Mesh LoadMesh(JToken source, string name)
        {
            float[] v = source["vertices"].ToObject<float[]>();
            float[] n = source["normals"].ToObject<float[]>();
            float[] uv = source["uv"].ToObject<float[]>();
            int count = v.Length / 3;
            if (n.Length != v.Length || uv.Length != count * 2) throw new InvalidDataException("mesh arrays");

            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                vertices[i] = new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2]);
                normals[i] = new Vector3(n[3 * i], n[3 * i + 1], n[3 * i + 2]);
                uvs[i] = new Vector2(uv[2 * i], uv[2 * i + 1]);
            }

            var mesh = new Mesh { name = "KeepMeAlive_" + name, hideFlags = HideFlags.HideAndDontSave };
            if (count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = source["triangles"].ToObject<int[]>();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.UploadMeshData(true);
            return mesh;
        }

        //====================[ Material ]====================
        // The material in an embedded bundle, authored on the game's own item shader. Its shader and reflection map are
        // references into the game's own bundles, found when it loads; a shader that did not resolve (the game's shader bundle
        // not loaded) counts as no material. Null, logged, when it cannot be used. The caller keeps the bundle and unloads it
        // (Unload) at the raid's end: it goes with its material, and a bundle can only be loaded once at a time.
        // Read straight from the DLL's resource stream (no managed copy of the bundle); Unity reads from that stream for as long
        // as the bundle is loaded, so the caller keeps it too and Unload closes it.
        public static Material LoadBundledMaterial(string tag, string resource, string path, out AssetBundle bundle, out Stream source)
        {
            bundle = null;
            source = null;
            try
            {
                source = EmbeddedData.Open(resource);
                bundle = AssetBundle.LoadFromStream(source);
                if (bundle == null)
                {
                    Plugin.LogSource.LogWarning($"[{tag}] The material bundle did not load.");
                    Unload(ref bundle, ref source);
                    return null;
                }

                var material = bundle.LoadAsset<Material>(path);
                if (material == null)
                {
                    var all = bundle.LoadAllAssets<Material>();
                    material = all.Length > 0 ? all[0] : null;
                }

                var shader = material != null ? material.shader : null;
                if (shader == null || !shader.isSupported || shader.name.StartsWith("Hidden/InternalError", StringComparison.Ordinal))
                {
                    Plugin.LogSource.LogWarning($"[{tag}] The material bundle's shader did not resolve ({(shader != null ? shader.name : "none")}).");
                    Unload(ref bundle, ref source);
                    return null;
                }

                RevivalDebugLog.LogDebug($"[{tag}] material from the bundle, shader {shader.name}, cubemap {(material.HasProperty("_Cube") && material.GetTexture("_Cube") != null)}");
                return material;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[{tag}] The material bundle could not be used: {ex.Message}");
                Unload(ref bundle, ref source);
                return null;
            }
        }

        // A prop's shared material: the bundle's, loaded on first use and dropped at the raid's end.
        public sealed class SharedMaterial
        {
            private readonly string _tag, _bundleResource, _bundlePath;
            private Material _material;
            private AssetBundle _bundle;
            private Stream _source;

            public SharedMaterial(string tag, string bundleResource, string bundlePath)
            {
                _tag = tag;
                _bundleResource = bundleResource;
                _bundlePath = bundlePath;
            }

            // The bundle's material; null, logged, when the bundle cannot be used (the prop is then not shown).
            public Material Get()
            {
                if (_material != null) return _material;
                _material = LoadBundledMaterial(_tag, _bundleResource, _bundlePath, out _bundle, out _source);
                return _material;
            }

            // Raid boundary: the material was made from a raid asset.
            public void Clear()
            {
                // The bundle's own material goes with the bundle.
                Unload(ref _bundle, ref _source);
                _material = null;
            }
        }

        // The bundle first, then the stream it reads from.
        public static void Unload(ref AssetBundle bundle, ref Stream source)
        {
            if (bundle != null) bundle.Unload(true);
            bundle = null;
            source?.Dispose();
            source = null;
        }

        //====================[ Renderer ]====================
        // A hidden object with a mesh and a material, on the layer the game puts items in hands on (UpdateBonesOnWeaponChange),
        // so every camera draws it, and drawn as the game draws its own items: shadows cast and received, lit by the probes.
        public static GameObject CreateProp(string name, Mesh mesh, Material material, int fallbackLayer)
        {
            var go = new GameObject(name);
            int layer = LayerMask.NameToLayer("Player");
            go.layer = layer >= 0 ? layer : fallbackLayer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            go.SetActive(false);
            return go;
        }
    }
}
