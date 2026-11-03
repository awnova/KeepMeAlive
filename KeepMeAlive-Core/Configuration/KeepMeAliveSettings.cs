//====================[ Imports ]====================
using BepInEx.Configuration;
using UnityEngine;

namespace KeepMeAlive.Configuration
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

        // Language
        public static ConfigEntry<string> LANGUAGE;

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

            //====================[ Language ]====================
            // The choices are the language files found at startup; a saved choice whose file is gone resets to Auto.
            LANGUAGE = config.Bind(
                "3. Language",
                "Language",
                Localization.Auto,
                new ConfigDescription("Language of KeepMeAlive's on-screen text. Auto follows the game's language when a file for it exists, else English. More languages: add <code>.json files to BepInEx/plugins/KeepMeAlive-Locales and restart.", new AcceptableValueList<string>(Localization.Choices()))
            );
            LANGUAGE.SettingChanged += (_, _) =>
            {
                Hud.RefreshText();
                FikaOverlayController.RefreshText();
                PostReviveEffects.RefreshCooldownEffectName();
            };
        }
    }
}
