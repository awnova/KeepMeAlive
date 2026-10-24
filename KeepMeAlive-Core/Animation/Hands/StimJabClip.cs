//====================[ Imports ]====================
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ StimJabClip ]====================
    // The dragger's right hand taking a stim pen from their chest and jabbing it into the dragged body's shoulder
    // (Build/Tools/DragPose/bake_stim_jab.py from Build/Assets/Downed/Drag_Stim_Jab.blend). Only the hand is baked: where the palm is
    // and which way it faces, the finger bones, and the pen. The arm is not; DraggerArms reaches the palm to where this puts
    // it and the arm follows.
    // The palm's place is given twice, as numbers along named physical axes so it means the same on any skeleton: in the
    // held body's chest frame (left, up the spine, out of its upper side), measured from the right grip point, and in the
    // dragger's own chest frame (left, up the spine, forward), measured from its upper chest bone; ChestWeight is how much of
    // it is the second. The hand starts on the body, goes to the chest to take the pen, and comes back to the body.
    internal sealed class StimJabClip
    {
        private const string Resource = "KeepMeAlive.Resources.drag_stim_jab.json";
        private const string Schema = "keepmealive.stimjab.v1";

        public struct Key
        {
            // The palm's contact point and the hand's directions (fingers, out of the palm).
            public Vector3 Contact, Fingers, Palm;
        }

        public float Fps;
        public int Frames;
        // Frame numbers (0-based) of the marks: the hand is on its way from the body at Release, at the chest at Pocket, the
        // needle goes in at Jab, and the pen is in the hand from PenShown.
        public float Release, Pocket, Jab, PenShown;
        public string[] Bones;
        private Key[] _body, _chest;
        private float[] _chestWeight;
        // [frame][bone]: the bone's rotation relative to the hand frame, from its rest pose (DraggerArms.ApplyFingers).
        private Quaternion[][] _fingers;

        //====================[ Instance ]====================
        public static StimJabClip Instance => EmbeddedData.Get(Resource, nameof(StimJabClip), Load);

        public float Duration => (Frames - 1) / Fps;

        // The two places of the palm, their weight, and the finger rotations, at a (fractional) frame.
        public void Sample(float frame, out Key body, out Key chest, out float chestWeight, Quaternion[] fingers)
        {
            float f = global::KeepMeAlive.Animation.Shared.MathUtil.SplitKey(frame, Frames, out int a, out int b);
            body = Lerp(_body[a], _body[b], f);
            chest = Lerp(_chest[a], _chest[b], f);
            chestWeight = Mathf.Lerp(_chestWeight[a], _chestWeight[b], f);
            for (int i = 0; i < fingers.Length && i < Bones.Length; i++)
                fingers[i] = Quaternion.Slerp(_fingers[a][i], _fingers[b][i], f);
        }

        private static Key Lerp(Key a, Key b, float f) => new Key
        {
            Contact = Vector3.Lerp(a.Contact, b.Contact, f),
            Fingers = Vector3.Lerp(a.Fingers, b.Fingers, f).normalized,
            Palm = Vector3.Lerp(a.Palm, b.Palm, f).normalized
        };

        //====================[ Loading ]====================
        private static StimJabClip Load()
        {
            var doc = EmbeddedData.ReadJson(Resource, Schema);
            var frames = (JArray)doc["frames"];
            var bones = (JArray)doc["bones"];
            var clip = new StimJabClip
            {
                Fps = (float)doc["fps"],
                Frames = frames.Count,
                Bones = bones.ToObject<string[]>(),
                Release = (float)doc["marks"]["release"],
                Pocket = (float)doc["marks"]["pocket"],
                Jab = (float)doc["marks"]["jab"],
                PenShown = (float)doc["marks"]["penShown"]
            };
            if (clip.Fps <= 0f || clip.Frames < 2 || clip.Bones.Length == 0) throw new InvalidDataException("bad timing");

            clip._body = new Key[clip.Frames];
            clip._chest = new Key[clip.Frames];
            clip._chestWeight = new float[clip.Frames];
            clip._fingers = new Quaternion[clip.Frames][];
            for (int i = 0; i < clip.Frames; i++)
            {
                var frame = (JObject)frames[i];
                clip._body[i] = ReadKey((JArray)frame["body"]);
                clip._chest[i] = ReadKey((JArray)frame["chest"]);
                clip._chestWeight[i] = (float)frame["chestWeight"];
                var fingers = (JArray)frame["fingers"];
                if (fingers.Count != clip.Bones.Length) throw new InvalidDataException("finger track length");
                clip._fingers[i] = new Quaternion[fingers.Count];
                for (int k = 0; k < fingers.Count; k++)
                {
                    clip._fingers[i][k] = EmbeddedData.ReadQuaternion(fingers[k]);
                }
            }
            return clip;
        }

        private static Key ReadKey(JArray v)
        {
            if (v.Count != 9) throw new InvalidDataException("palm key length");
            return new Key
            {
                Contact = new Vector3((float)v[0], (float)v[1], (float)v[2]),
                Fingers = new Vector3((float)v[3], (float)v[4], (float)v[5]).normalized,
                Palm = new Vector3((float)v[6], (float)v[7], (float)v[8]).normalized
            };
        }
    }
}
