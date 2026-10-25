//====================[ Imports ]====================

namespace KeepMeAlive.Presentation.Messaging
{
    //====================[ PlayerFacingMessages ]====================
    // Centralized catalog for player-visible text.
    internal static class PlayerFacingMessages
    {
        private static string LifeWord(int count) => count == 1 ? "life" : "lives";

        //====================[ HUD ]====================
        // Labels drawn by Hud. Shown upper-case by the bar's font style.
        internal static class HudLabels
        {
            public const string BleedingOut = "Bleeding out";
            public const string BeingRevived = "Being revived";
            public const string Reviving = "Reviving";
            public const string RevivingYourself = "Reviving yourself";
            public const string Invulnerable = "Invulnerable";
            public const string GivingUp = "Giving up";
            public const string Healing = "Healing";

            public const string SelfRevive = "Self-revive";
            public const string Revive = "Revive";
            public const string Drag = "Drag";
            public const string Loot = "Loot";
            public const string Tap = "Tap";
            public const string GiveUp = "Give up";
            public const string Hold = "Hold";
            public const string WaitForTeammate = "Wait for teammate";
            public const string NeedsStim = "Needs stim";
            public const string NeedsDefib = "Needs defib";

            public const string Spectate = "Spectate";
            public const string Spectating = "Spectating";
            public const string Switch = "Switch";
            public const string Camera = "Camera";
            public const string NightVision = "Night vision";
            public const string ReturnToBody = "Return to body";
            public const string Head = "Head";
            public const string ThirdPerson = "3rd person";
            public const string HeadCam = "Head cam";
            public const string On = "On";
            public const string Off = "Off";

            public static string LivesLeft(int count) => $"{count} {LifeWord(count)} left";
            public static string Cost(int count) => $"-{count} {LifeWord(count)}";
            public const string NotEnoughLives = "Not enough lives";
        }

        //====================[ Interactions ]====================
        internal static class Interaction
        {
            public const string CannotReviveWhileMoving = "You can't revive a player while moving";
            // The one menu entry for a downed teammate: tap drags (or lets go), hold revives.
            public const string SupportAction = "Support";
            public const string ReviveNoLongerPossible = "Revive no longer possible";
            // The one menu entry for a standing, hurt teammate: opens the team-heal wheel.
            public const string InteractAction = "Interact";

            public const string DragHint = "Dragging teammate - stand up, draw a weapon or turn away to let go";
            public const string DragReleased = "Released teammate";
            public const string LootProtectedItem = "Equipped gear can't be taken from a downed teammate";
        }

        //====================[ Team Heal ]====================
        internal static class TeamHeal
        {
            public const string PatientUnavailable = "Patient is no longer available";
            public const string HealNoResponse = "Heal not confirmed by teammate";
            public const string ItemUnavailable = "Med item no longer available";
            public const string PatientBusy = "Teammate could not use the item";
            public const string HealInterrupted = "Teammate stopped using the item";

            public static string StartedBy(string healerDisplay, string patientDisplay) =>
                $"{healerDisplay} is healing {patientDisplay}";
        }

        //====================[ Team-Heal Wheel ]====================
        // Labels drawn by TeamHealWheelView and its strip. Shown upper-case by the font style.
        internal static class HealWheel
        {
            public const string Bleeds = "Bleeds";
            public const string Breaks = "Breaks";
            public const string Health = "Health";
            public const string Comfort = "Comfort";
            public const string Nutrition = "Nutrition";

            public const string NotNeeded = "Not needed";
            public const string NoMeds = "No meds";

            public const string Open = "Open";
            public const string Close = "Close";
            public const string Use = "Use";
            public const string Back = "Back";

            public const string Hp = "HP";
            public const string Uses = "uses";
            public const string OneUse = "1 use";

            public static string Items(int count) => count == 1 ? "1 item" : $"{count} items";
        }

        //====================[ Revive Flow ]====================
        internal static class Revive
        {
            public const string MissingStimCanceled = "Stim missing. Revive canceled.";
            public const string Disabled = "This kind of revive is disabled";
            public const string MissingDefibCanceled = "Defib missing. Revive canceled.";
        }

        //====================[ Revive Complete ]====================
        //====================[ Revive Denials ]====================
        internal static class ReviveDenied
        {
            public const string AuthorizationFailed = "Revive authorization failed";
            public const string TeamFallback = "Revive denied";
            public const string SelfFallback = "Revive denied by server";
            public const string Cooldown = "Player on cooldown";
            public const string OutOfLives = "Out of lives";
        }

        //====================[ Network Revive ]====================
        internal static class NetworkRevive
        {
            public static string TeamCancelledBy(string reviverName) =>
                $"{reviverName} cancelled revival";
        }

        //====================[ Downed UI ]====================
        internal static class Downed
        {
            public const string ReviverTimedOut = "Reviver disconnected or timed out.";

            // Shown to everyone else when a teammate goes down.
            public static string TeammateDown(string name) => $"{name} is down!";
        }

        //====================[ Post-Revive ]====================
        internal static class PostRevive
        {
            public const string CooldownEnded = "Revival cooldown ended - you can now be revived";

            public static string InvulnerabilityEnded(float cooldownSeconds) =>
                $"Invulnerability ended. Revival cooldown: {cooldownSeconds:F0}s";
        }

        //====================[ Death ]====================
        //====================[ Spectate ]====================
        internal static class Spectate
        {
            public const string NoTeammates = "No teammates left to spectate";
        }
    }
}
