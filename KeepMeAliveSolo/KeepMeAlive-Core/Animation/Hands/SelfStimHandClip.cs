//====================[ Imports ]====================
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ SelfStimHandClip ]====================
    // The right hand of the self-revive clip (Build/Tools/SelfStim/bake_self_stim_hand.py from Build/Assets/Downed/Self_Stim.blend): the fifteen
    // finger bones per frame, and the clip's marks. The body (spine, arms, legs) is the clip BakedClip.SelfStim, which drives no
    // fingers; frames here are the same frames, index = frame, so a time in that clip is a frame here.
    internal sealed class SelfStimHandClip
    {
        private const string Resource = "KeepMeAlive.Resources.self_stim_hand.json";
        private const string Schema = "keepmealive.selfstim.v1";

        public float Fps;
        public int Frames;
        // Frame numbers (0-based): the pen is in the closed hand from PenShown up to (not including) PenStow, the needle holds in
        // the chest from HoldStart to HoldEnd (the loop: the same pose at both), and the clip ends on End.
        public float PenShown, PenStow, HoldStart, HoldEnd, End;
        public string[] Bones;
        // [frame][bone]: the bone's rotation relative to the hand frame, from its rest pose (HandFingers).
        private Quaternion[][] _fingers;

        //====================[ Instance ]====================
        public static SelfStimHandClip Instance => EmbeddedData.Get(Resource, nameof(SelfStimHandClip), Load);

        // The finger rotations at a (fractional) frame.
        public void Sample(float frame, Quaternion[] fingers)
        {
            float f = global::KeepMeAlive.Animation.Shared.MathUtil.SplitKey(frame, Frames, out int a, out int b);
            for (int i = 0; i < fingers.Length && i < Bones.Length; i++)
                fingers[i] = Quaternion.Slerp(_fingers[a][i], _fingers[b][i], f);
        }

        //====================[ Loading ]====================
        private static SelfStimHandClip Load()
        {
            var doc = EmbeddedData.ReadJson(Resource, Schema);
            var frames = (JArray)doc["frames"];
            var marks = doc["marks"];
            var clip = new SelfStimHandClip
            {
                Fps = (float)doc["fps"],
                Frames = frames.Count,
                Bones = ((JArray)doc["bones"]).ToObject<string[]>(),
                PenShown = (float)marks["penShown"],
                PenStow = (float)marks["penStow"],
                HoldStart = (float)marks["holdStart"],
                HoldEnd = (float)marks["holdEnd"],
                End = (float)marks["end"]
            };
            if (clip.Fps <= 0f || clip.Frames < 2 || clip.Bones.Length == 0) throw new InvalidDataException("bad timing");
            if (clip.HoldStart >= clip.HoldEnd || clip.HoldEnd > clip.End || clip.End > clip.Frames - 1) throw new InvalidDataException("bad marks");

            clip._fingers = new Quaternion[clip.Frames][];
            for (int i = 0; i < clip.Frames; i++)
            {
                var fingers = (JArray)frames[i];
                if (fingers.Count != clip.Bones.Length) throw new InvalidDataException("finger track length");
                clip._fingers[i] = new Quaternion[fingers.Count];
                for (int k = 0; k < fingers.Count; k++)
                    clip._fingers[i][k] = EmbeddedData.ReadQuaternion(fingers[k]);
            }
            return clip;
        }
    }
}
