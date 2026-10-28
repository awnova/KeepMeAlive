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
            public static string BeingRevived => T("Hud.BeingRevived");
            public static string Reviving => T("Hud.Reviving");
            public static string RevivingYourself => T("Hud.RevivingYourself");
            public static string Invulnerable => T("Hud.Invulnerable");
            public static string GivingUp => T("Hud.GivingUp");
            public static string Healing => T("Hud.Healing");

            public static string SelfRevive => T("Hud.SelfRevive");
            public static string Revive => T("Hud.Revive");
            public static string Drag => T("Hud.Drag");
            public static string Loot => T("Hud.Loot");
            public static string Tap => T("Hud.Tap");
            public static string GiveUp => T("Hud.GiveUp");
            public static string Hold => T("Hud.Hold");
            public static string WaitForTeammate => T("Hud.WaitForTeammate");
            public static string NeedsStim => T("Hud.NeedsStim");
            public static string NeedsDefib => T("Hud.NeedsDefib");

            public static string Spectate => T("Hud.Spectate");
            public static string Spectating => T("Hud.Spectating");
            public static string Switch => T("Hud.Switch");
            public static string Camera => T("Hud.Camera");
            public static string NightVision => T("Hud.NightVision");
            public static string ReturnToBody => T("Hud.ReturnToBody");
            public static string Head => T("Hud.Head");
            public static string ThirdPerson => T("Hud.ThirdPerson");
            public static string HeadCam => T("Hud.HeadCam");
            public static string On => T("Hud.On");
            public static string Off => T("Hud.Off");

            public static string LivesLeft(int count) => Count("Hud.LivesLeft", count);
            public static string Cost(int count) => Count("Hud.Cost", count);
            public static string NotEnoughLives => T("Hud.NotEnoughLives");
        }

        //====================[ Interactions ]====================
        internal static class Interaction
        {
            public static string CannotReviveWhileMoving => T("Interaction.CannotReviveWhileMoving");
            // The one menu entry for a downed teammate: tap drags (or lets go), hold revives.
            public static string SupportAction => T("Interaction.Support");
            public static string ReviveNoLongerPossible => T("Interaction.ReviveNoLongerPossible");
            // The one menu entry for a standing, hurt teammate: opens the team-heal wheel.
            public static string InteractAction => T("Interaction.Interact");

            public static string DragHint => T("Interaction.DragHint");
            public static string DragReleased => T("Interaction.DragReleased");
            public static string LootProtectedItem => T("Interaction.LootProtectedItem");
        }

        //====================[ Team Heal ]====================
        internal static class TeamHeal
        {
            public static string PatientUnavailable => T("TeamHeal.PatientUnavailable");
            public static string HealNoResponse => T("TeamHeal.HealNoResponse");
            public static string ItemUnavailable => T("TeamHeal.ItemUnavailable");
            public static string PatientBusy => T("TeamHeal.PatientBusy");
            public static string HealInterrupted => T("TeamHeal.HealInterrupted");

            public static string StartedBy(string healerDisplay, string patientDisplay) =>
                F("TeamHeal.StartedBy", healerDisplay, patientDisplay);
        }

        //====================[ Team-Heal Wheel ]====================
        // Labels drawn by TeamHealWheelView and its strip. Shown upper-case by the font style.
        internal static class HealWheel
        {
            public static string Bleeds => T("HealWheel.Bleeds");
            public static string Breaks => T("HealWheel.Breaks");
            public static string Health => T("HealWheel.Health");
            public static string Comfort => T("HealWheel.Comfort");
            public static string Nutrition => T("HealWheel.Nutrition");

            public static string NotNeeded => T("HealWheel.NotNeeded");
            public static string NoMeds => T("HealWheel.NoMeds");

            public static string Open => T("HealWheel.Open");
            public static string Close => T("HealWheel.Close");
            public static string Use => T("HealWheel.Use");
            public static string Back => T("HealWheel.Back");

            public static string Hp => T("HealWheel.Hp");
            public static string Uses => T("HealWheel.Uses");
            public static string OneUse => T("HealWheel.OneUse");

            public static string Items(int count) => Count("HealWheel.Items", count);
        }

        //====================[ Revive Flow ]====================
        internal static class Revive
        {
            public static string MissingStimCanceled => T("Revive.MissingStimCanceled");
            public static string Disabled => T("Revive.Disabled");
            public static string MissingDefibCanceled => T("Revive.MissingDefibCanceled");
        }

        //====================[ Revive Denials ]====================
        internal static class ReviveDenied
        {
            public static string AuthorizationFailed => T("ReviveDenied.AuthorizationFailed");
            public static string TeamFallback => T("ReviveDenied.TeamFallback");
            public static string SelfFallback => T("ReviveDenied.SelfFallback");
            public static string Cooldown => T("ReviveDenied.Cooldown");
            public static string OutOfLives => T("ReviveDenied.OutOfLives");
        }

        //====================[ Network Revive ]====================
        internal static class NetworkRevive
        {
            public static string TeamCancelledBy(string reviverName) => F("NetworkRevive.TeamCancelledBy", reviverName);
        }

        //====================[ Downed UI ]====================
        internal static class Downed
        {
            public static string ReviverTimedOut => T("Downed.ReviverTimedOut");

            // Shown to everyone else when a teammate goes down.
            public static string TeammateDown(string name) => F("Downed.TeammateDown", name);
        }

        //====================[ Post-Revive ]====================
        internal static class PostRevive
        {
            public static string CooldownEnded => T("PostRevive.CooldownEnded");

            public static string InvulnerabilityEnded(float cooldownSeconds) =>
                F("PostRevive.InvulnerabilityEnded", cooldownSeconds);
        }

        //====================[ Spectate ]====================
        internal static class Spectate
        {
            public static string NoTeammates => T("Spectate.NoTeammates");
        }
    }
}
