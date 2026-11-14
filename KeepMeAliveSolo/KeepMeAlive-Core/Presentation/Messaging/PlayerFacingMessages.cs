//====================[ Imports ]====================

namespace KeepMeAlive.Presentation.Messaging
{
    //====================[ PlayerFacingMessages ]====================
    // Centralized catalog for player-visible text. The wording lives in Resources/Localization/en.json (and its
    // translations); each entry here is read when shown, so a language change applies to the next text drawn.
    internal static class PlayerFacingMessages
    {
        private static string T(string key) => Localization.Text(key);
        private static string F(string key, params object[] args) => Localization.Format(key, args);
        // A count with its noun, in the form the language uses for that number (key.One / .Few / .Other).
        private static string Count(string key, int count) => Localization.Plural(key, count);

        //====================[ HUD ]====================
        // Labels drawn by Hud. Shown upper-case by the bar's font style.
        internal static class HudLabels
        {
            public static string BleedingOut => T("Hud.BleedingOut");
            public static string Reviving => T("Hud.Reviving");
            public static string RevivingYourself => T("Hud.RevivingYourself");
            public static string Invulnerable => T("Hud.Invulnerable");
            public static string GivingUp => T("Hud.GivingUp");

            public static string SelfRevive => T("Hud.SelfRevive");
            public static string GiveUp => T("Hud.GiveUp");
            public static string Redeploy => T("Hud.Redeploy");
            public static string Redeploying => T("Hud.Redeploying");
            public static string Hold => T("Hud.Hold");
            public static string NeedsStim => T("Hud.NeedsStim");

            public static string LivesLeft(int count) => Gameplay.Revival.RevivePolicy.IsUnlimited(count) ? F("Hud.LivesLeft.Other", "∞") : Count("Hud.LivesLeft", count);
            public static string Cost(int count) => Count("Hud.Cost", count);
            public static string NotEnoughLives => T("Hud.NotEnoughLives");
        }

        //====================[ Revive Flow ]====================
        internal static class Revive
        {
            public static string MissingStimCanceled => T("Revive.MissingStimCanceled");
            public static string Disabled => T("Revive.Disabled");
            public static string InventoryBusyCanceled => T("Revive.InventoryBusyCanceled");
        }

        //====================[ Redeploy ]====================
        internal static class Redeploy
        {
            public static string NoSpawn => T("Redeploy.NoSpawn");
            public static string Failed => T("Redeploy.Failed");
        }

        //====================[ Revive Denials ]====================
        internal static class ReviveDenied
        {
            public static string AuthorizationFailed => T("ReviveDenied.AuthorizationFailed");
            public static string SelfFallback => T("ReviveDenied.SelfFallback");
            public static string Cooldown => T("ReviveDenied.Cooldown");
            public static string OutOfLives => T("ReviveDenied.OutOfLives");
        }

        //====================[ Post-Revive ]====================
        internal static class PostRevive
        {
            public static string CooldownEnded => T("PostRevive.CooldownEnded");

            public static string InvulnerabilityEnded(float cooldownSeconds) =>
                F("PostRevive.InvulnerabilityEnded", cooldownSeconds);

            // The cooldown effect's name on its health-panel tooltip.
            public static string CooldownEffectName => T("PostRevive.CooldownEffectName");
        }
    }
}
