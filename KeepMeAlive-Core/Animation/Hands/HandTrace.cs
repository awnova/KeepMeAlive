//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Animation.Hands
{
    //====================[ HandTrace ]====================
    // TEMPORARY: a trace of the hands the mod poses (the defibrillator, the dragger's arms and the stim jab), at Info level so
    // it shows in the BepInEx log without turning debug logging on. For reading where the hands really are in the game
    // against where the baked data puts them. Take it out (or turn Enabled off) once the hands are right.
    internal static class HandTrace
    {
        private const bool Enabled = true;
        // Seconds between the per-frame lines of one trace.
        public const float Interval = 0.5f;

        public static bool On => Enabled;

        public static void Log(string message)
        {
            if (Enabled) Plugin.LogSource.LogInfo("[HandTrace] " + message);
        }

        public static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        // Metres to whole centimetres, for readable lines.
        public static string Cm(float metres) => $"{metres * 100f:F0}cm";
    }
}
