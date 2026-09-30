//====================[ Imports ]====================
using BepInEx.Configuration;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ KeepMeAliveSettings ]====================
    internal class KeepMeAliveSettings
    {
        //====================[ Local-Only Settings ]====================
        // Key Bindings
        public static ConfigEntry<KeyCode> SELF_REVIVAL_KEY;
        public static ConfigEntry<KeyCode> GIVE_UP_KEY;

        // Audio
        public static ConfigEntry<bool> ENABLE_TINNITUS_EFFECT;

        // Visuals
        public static ConfigEntry<bool> ENABLE_DOWNED_SCREEN_EFFECTS;
        public static ConfigEntry<bool> ENABLE_DOWNED_GRAYSCALE;
        public static ConfigEntry<float> DOWNED_GRAYSCALE_STRENGTH;
        public static ConfigEntry<float> DOWNED_DIM_STRENGTH;
        public static ConfigEntry<float> DOWNED_PULSE_MIN_STRENGTH;
        public static ConfigEntry<float> DOWNED_PULSE_OFFSET_MS;

        // Debug / Diagnostics
        public static ConfigEntry<bool> ENABLE_DEBUG_LOGS;
        public static ConfigEntry<bool> DEBUG_REVIVE_FLOW;
        public static ConfigEntry<bool> DEBUG_NETWORK_TRACE;
        public static ConfigEntry<bool> DEBUG_SELF_REVIVE_TRACE;
        public static ConfigEntry<bool> DEBUG_KEYBINDS;

        //====================[ Init ]====================
        public static void Init(ConfigFile config)
        {
            //====================[ Key Bindings ]====================
            SELF_REVIVAL_KEY = config.Bind(
                "1. Key Bindings",
                "Self Revival Key",
                KeyCode.F,
                "The key to press and hold to revive yourself when in critical state"
            );

            GIVE_UP_KEY = config.Bind(
                "1. Key Bindings",
                "Give Up Key",
                KeyCode.Backspace,
                "Press this key when in critical state to die immediately"
            );

            //====================[ Audio ]====================
            ENABLE_TINNITUS_EFFECT = config.Bind(
                "2. Audio",
                "Enable Tinnitus Ring",
                true,
                "Enables the vanilla tinnitus ring sound effect when downed/contused. The ring is already capped to its shortest possible duration; disable this to mute it entirely and rely on the heartbeat cue instead."
            );

            //====================[ Visuals ]====================
            ENABLE_DOWNED_SCREEN_EFFECTS = config.Bind(
                "3. Visuals",
                "Enable Downed Screen Effects",
                true,
                "Pulsing tunnel vision, low-health tint and red screen edge while downed. Local only; does not affect other players."
            );

            ENABLE_DOWNED_GRAYSCALE = config.Bind(
                "3. Visuals",
                "Enable Downed Grayscale",
                true,
                "Desaturates the game world while downed. Blood on screen and the on-screen timers are not affected. Requires Downed Screen Effects."
            );

            DOWNED_GRAYSCALE_STRENGTH = config.Bind(
                "3. Visuals",
                "Downed Grayscale Strength",
                1f,
                new ConfigDescription("How strongly the world is desaturated while downed (0 = none, 1 = full).", new AcceptableValueRange<float>(0f, 1f))
            );

            DOWNED_DIM_STRENGTH = config.Bind(
                "3. Visuals",
                "Downed Dim Strength",
                0.3f,
                new ConfigDescription("How much the desaturated world is also darkened while downed (0 = none, 1 = fully black at the end of the timer). Follows the grayscale fade, so it needs Downed Grayscale enabled.", new AcceptableValueRange<float>(0f, 1f))
            );

            DOWNED_PULSE_MIN_STRENGTH = config.Bind(
                "3. Visuals",
                "Downed Pulse Minimum Strength",
                0.9f,
                new ConfigDescription("The vignette and red screen edge stay at full strength and ease down to this fraction only in the gap between the heartbeat's lub and dub, then back up (1 = steady, 0.9 = dips to 90%).", new AcceptableValueRange<float>(0f, 1f))
            );

            DOWNED_PULSE_OFFSET_MS = config.Bind(
                "3. Visuals",
                "Heartbeat Visual Offset (ms)",
                0f,
                new ConfigDescription("Shifts the visual pulse relative to the heartbeat sound. Positive = later, negative = earlier. Use if the pulse feels out of sync with the audio.", new AcceptableValueRange<float>(-100f, 150f), new ConfigurationManagerAttributes { IsAdvanced = true })
            );

            //====================[ Debug ]====================
            ENABLE_DEBUG_LOGS = config.Bind(
                "5. Development",
                "Enable Debug Logs",
                false,
                new ConfigDescription("Enables debug logging output for revival systems", null, new ConfigurationManagerAttributes { IsAdvanced = true })
            );

            DEBUG_REVIVE_FLOW = config.Bind(
                "5. Development",
                "Debug Revive Flow",
                false,
                new ConfigDescription("Logs detailed revive flow transitions (requires Enable Debug Logs)", null, new ConfigurationManagerAttributes { IsAdvanced = true })
            );

            DEBUG_NETWORK_TRACE = config.Bind(
                "5. Development",
                "Debug Network Trace",
                false,
                new ConfigDescription("Logs revive-related network packet traces (requires Enable Debug Logs)", null, new ConfigurationManagerAttributes { IsAdvanced = true })
            );

            DEBUG_SELF_REVIVE_TRACE = config.Bind(
                "5. Development",
                "Debug Self Revive Trace",
                false,
                new ConfigDescription("Logs self-revive lifecycle details (requires Enable Debug Logs)", null, new ConfigurationManagerAttributes { IsAdvanced = true })
            );

            DEBUG_KEYBINDS = config.Bind(
                "5. Development",
                "Debug Keybinds",
                false,
                new ConfigDescription("Enables debug keybinds: F7=Enter Ghost Mode, F8=Exit Ghost Mode", null, new ConfigurationManagerAttributes { IsAdvanced = true })
            );
        }
    }
}
