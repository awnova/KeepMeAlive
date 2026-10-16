//====================[ Imports ]====================
using System;
using System.IO;
using KeepMeAlive.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace KeepMeAlive.Features
{
    //====================[ HandProps ]====================
    // What the props the mod puts in a player's hands (the defibrillator paddles, the stim pen) are made of, in one place: a
    // mesh from baked JSON (Tools/DefibHands/bake_defib_hands.py, Tools/DragPose/bake_stim_jab.py), a texture from an embedded
    // PNG, a material from an embedded bundle (Tools/PropBundles/build_prop_bundles.py) and a renderer set up the way the game
    // sets up its own items.
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

            var mesh = new Mesh { name = "KeepMeAlive_" + name };
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

        //====================[ Texture ]====================
        public static Texture2D LoadTexture(string resource, string name)
        {
            using var stream = EmbeddedData.Open(resource);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "KeepMeAlive_" + name };
            if (!texture.LoadImage(memory.ToArray(), true)) throw new InvalidDataException("texture decode");
            return texture;
        }

        //====================[ Material ]====================
        // The material in an embedded bundle, authored on the game's own item shader. Its shader and reflection map are
        // references into the game's own bundles, found when it loads; a shader that did not resolve (the game's shader bundle
        // not loaded) counts as no material. Null, logged, when it cannot be used. The caller keeps the bundle and unloads it
        // (Unload) at the raid's end: it goes with its material, and a bundle can only be loaded once at a time.
        public static Material LoadBundledMaterial(string tag, string resource, string path, out AssetBundle bundle)
        {
            bundle = null;
            try
            {
                using var stream = EmbeddedData.Open(resource);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bundle = AssetBundle.LoadFromMemory(memory.ToArray());
                if (bundle == null)
                {
                    Plugin.LogSource.LogWarning($"[{tag}] The material bundle did not load.");
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
                    Unload(ref bundle);
                    return null;
                }

                RevivalDebugLog.LogDebug($"[{tag}] material from the bundle, shader {shader.name}, cubemap {(material.HasProperty("_Cube") && material.GetTexture("_Cube") != null)}");
                return material;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[{tag}] The material bundle could not be used: {ex.Message}");
                Unload(ref bundle);
                return null;
            }
        }

        // A prop's shared material: the bundle's, else a plain one, built on first use and dropped at the raid's end.
        public sealed class SharedMaterial
        {
            private readonly string _tag, _bundleResource, _bundlePath, _textureResource, _textureName;
            private Material _material;
            private AssetBundle _bundle;

            public SharedMaterial(string tag, string bundleResource, string bundlePath, string textureResource, string textureName)
            {
                _tag = tag;
                _bundleResource = bundleResource;
                _bundlePath = bundlePath;
                _textureResource = textureResource;
                _textureName = textureName;
            }

            // The bundle's material, else a plain one so the prop still shows. Null only without any shader.
            public Material Get()
            {
                if (_material != null) return _material;
                _material = LoadBundledMaterial(_tag, _bundleResource, _bundlePath, out _bundle)
                    ?? PlainMaterial(_tag, LoadTexture(_textureResource, _textureName));
                return _material;
            }

            // Raid boundary: the material was made from a raid asset.
            public void Clear()
            {
                // The bundle's own material goes with the bundle.
                if (_bundle != null) Unload(ref _bundle);
                else if (_material != null) Object.Destroy(_material);
                _material = null;
            }
        }

        public static void Unload(ref AssetBundle bundle)
        {
            if (bundle != null) bundle.Unload(true);
            bundle = null;
        }

        // A plain lit material with the prop's colour, for when there is no bundled one, so the prop still shows. Null when the
        // build has none of the shaders it tries.
        public static Material PlainMaterial(string tag, Texture2D colour)
        {
            var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                Plugin.LogSource.LogWarning($"[{tag}] No material and no built-in shader; not shown.");
                return null;
            }
            Plugin.LogSource.LogWarning($"[{tag}] Using shader '{shader.name}' instead of the game's item material.");
            return new Material(shader) { name = "KeepMeAlive_" + tag, mainTexture = colour };
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
