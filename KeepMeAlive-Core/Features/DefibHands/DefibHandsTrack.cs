//====================[ Imports ]====================
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DefibHandsTrack ]====================
    // The reviver's hands through the defibrillator sequence, baked by Tools/DefibHands/bake_defib_hands.py from
    // Stand_To_Crouch_Defib: per frame, for each hand, the palm (wrist joint), the hand's forward and up axes and the
    // elbow, relative to the Ribcage (where the game's camera pivots) in the character frame (+Z facing, +Y up). The
    // hand's axes come from joint positions (palm -> middle knuckle; index -> little knuckle line), so they mean the
    // same on the game's skeleton. Also each hand's finger bones, relative to that hand frame from the rest pose (HandFingers):
    // the grip the paddles are fitted to.
    internal sealed class DefibHandsTrack
    {
        private const string Resource = "KeepMeAlive.Resources.defib_hands.json";
        private const string Schema = "keepmealive.defibhands.v3";
        private const int FloatsPerHand = 12;

        public struct Hand
        {
            public Vector3 Palm;
            public Vector3 Forward;
            public Vector3 Up;
            public Vector3 Elbow;
        }

        public float Fps;
        public int FirstFrame;
        public int LastFrame;
        // Pull out FirstFrame..RubStart; rub RubStart..RubEnd (loops: RubEnd is RubStart's pose); the charge's tail
        // RubEnd..ChargeEnd; shock ChargeEnd..ShockEnd; put away ShockEnd..LastFrame.
        public int RubStart;
        public int RubEnd;
        public int ChargeEnd;
        public int ShockEnd;
        // The paddles are in the hands from PaddlesShow until PaddlesHide.
        public int PaddlesShow;
        public int PaddlesHide;
        // Where the paddles meet at the start of the rub, in the track's frame: the point the view centres on.
        public Vector3 Aim;

        // The finger bones' names without the side ("Digit11"...), parents first.
        public string[] FingerBones;

        private float[][] _frames;
        // [frame][hand: left, right][bone]
        private Quaternion[][][] _fingers;

        //====================[ Loading ]====================
        // Loaded on first use; null (logged once) when it cannot be.
        public static DefibHandsTrack Instance => EmbeddedData.Get(Resource, nameof(DefibHandsTrack), Load);

        private static DefibHandsTrack Load()
        {
            var doc = EmbeddedData.ReadJson(Resource, Schema);
            var marks = (JObject)doc["marks"];
            var track = new DefibHandsTrack
            {
                Fps = (float)doc["fps"],
                FirstFrame = (int)doc["firstFrame"],
                LastFrame = (int)doc["lastFrame"],
                RubStart = (int)marks["rubStart"],
                RubEnd = (int)marks["rubEnd"],
                ChargeEnd = (int)marks["chargeEnd"],
                ShockEnd = (int)marks["shockEnd"],
                PaddlesShow = (int)doc["paddlesShow"],
                PaddlesHide = (int)doc["paddlesHide"],
            };
            var aim = (JArray)doc["aim"];
            track.Aim = new Vector3((float)aim[0], (float)aim[1], (float)aim[2]);
            if (track.Aim.sqrMagnitude < 1e-4f) throw new InvalidDataException("bad aim");

            var frames = (JArray)doc["frames"];
            if (track.Fps <= 0f || frames.Count != track.LastFrame - track.FirstFrame + 1) throw new InvalidDataException("bad timing");
            track._frames = new float[frames.Count][];
            for (int i = 0; i < frames.Count; i++)
            {
                var row = (JArray)frames[i];
                if (row.Count != 2 * FloatsPerHand) throw new InvalidDataException("bad frame " + i);
                track._frames[i] = row.ToObject<float[]>();
            }

            track.FingerBones = doc["fingerBones"].ToObject<string[]>();
            var fingers = (JArray)doc["fingers"];
            if (fingers.Count != frames.Count) throw new InvalidDataException("finger track length");
            track._fingers = new Quaternion[fingers.Count][][];
            for (int i = 0; i < fingers.Count; i++)
            {
                var row = (JArray)fingers[i];
                if (row.Count != 2) throw new InvalidDataException("finger frame " + i);
                track._fingers[i] = new Quaternion[2][];
                for (int hand = 0; hand < 2; hand++)
                {
                    var bones = (JArray)row[hand];
                    if (bones.Count != track.FingerBones.Length) throw new InvalidDataException("finger bones " + i);
                    track._fingers[i][hand] = new Quaternion[bones.Count];
                    for (int k = 0; k < bones.Count; k++)
                    {
                        track._fingers[i][hand][k] = EmbeddedData.ReadQuaternion(bones[k]);
                    }
                }
            }
            return track;
        }

        //====================[ Sampling ]====================
        // Seconds between two frames of the source action.
        public float Seconds(int fromFrame, int toFrame) => (toFrame - fromFrame) / Fps;

        // Both hands at a (fractional) frame of the source action, clamped to the baked range.
        public void Sample(float frame, out Hand left, out Hand right)
        {
            float t = MathUtil.SplitKey(frame - FirstFrame, _frames.Length, out int a, out int b);
            left = Lerp(_frames[a], _frames[b], 0, t);
            right = Lerp(_frames[a], _frames[b], FloatsPerHand, t);
        }

        // Both hands' finger rotations at a (fractional) frame, into the two arrays (left, right), one per FingerBones.
        public void SampleFingers(float frame, Quaternion[] left, Quaternion[] right)
        {
            float t = MathUtil.SplitKey(frame - FirstFrame, _frames.Length, out int a, out int b);
            for (int k = 0; k < FingerBones.Length; k++)
            {
                left[k] = Quaternion.Slerp(_fingers[a][0][k], _fingers[b][0][k], t);
                right[k] = Quaternion.Slerp(_fingers[a][1][k], _fingers[b][1][k], t);
            }
        }

        private static Hand Lerp(float[] a, float[] b, int offset, float t) => new Hand
        {
            Palm = Vector3.Lerp(Vec(a, offset), Vec(b, offset), t),
            Forward = Vector3.Lerp(Vec(a, offset + 3), Vec(b, offset + 3), t).normalized,
            Up = Vector3.Lerp(Vec(a, offset + 6), Vec(b, offset + 6), t).normalized,
            Elbow = Vector3.Lerp(Vec(a, offset + 9), Vec(b, offset + 9), t),
        };

        private static Vector3 Vec(float[] row, int i) => new Vector3(row[i], row[i + 1], row[i + 2]);
    }
}
