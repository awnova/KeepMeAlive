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
            public const string Tap = "Tap";
            public const string GiveUp = "Give up";
            public const string Hold = "Hold";
            public const string WaitForTeammate = "Wait for teammate";
            public const string NeedsDefibrillator = "Needs defibrillator";

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
            public static string NeedsLives(int count) => $"Needs {count} {LifeWord(count)}";
        }

        //====================[ Interactions ]====================
        internal static class Interaction
        {
            public const string TeamReviveDisabled = "Team revive is disabled";
            public const string CannotReviveWhileMoving = "You can't revive a player while moving";
            // The one menu entry for a downed teammate: tap drags (or lets go), hold revives.
            public const string SupportAction = "Support";
            public const string LootAction = "Loot";
            public const string ReviveNoLongerPossible = "Revive no longer possible";
            public const string ReviveCancelled = "Revive cancelled!";

            public const string MedicBleeds = "Medic Bleeds";
            public const string MedicBreaks = "Medic Breaks";
            public const string MedicHealth = "Medic Health";
            public const string MedicComfort = "Medic Comfort";
            public const string MedicNutrition = "Medic Nutrition";

            public const string MedPickerName = "Med Picker";
            public const string CancelAction = "Cancel";
            public const string DragHint = "Dragging teammate - stand up, draw a weapon or turn away to let go";
            public const string DragReleased = "Released teammate";
            public const string LootProtectedItem = "Equipped gear can't be taken from a downed teammate";
        }

        //====================[ Team Heal ]====================
        internal static class TeamHeal
        {
            public const string CannotHealWhileMoving = "You can't heal while moving";
            public const string HealingCancelled = "Healing cancelled!";
            public const string PatientUnavailable = "Patient is no longer available";
            public const string HealingTeammate = "Healing teammate...";
            public const string YouWereHealed = "You were healed!";
            public const string HealSucceeded = "Teammate healed";
            public const string HealNoResponse = "Heal not confirmed by teammate";
            public const string ItemUnavailable = "Med item no longer available";
            public const string PatientBusy = "Teammate could not use the item";

            public static string StartedBy(string healerDisplay, string patientDisplay) =>
                $"{healerDisplay} is healing {patientDisplay}";
        }

        //====================[ Revive Flow ]====================
        internal static class Revive
        {
            public const string NoReviveItemFound = "No revive item found! Unable to revive!";
            public const string SelfReviveCanceled = "Self-revive canceled";
            public const string MissingItemCanceled = "Revive Item missing. Self-revive canceled.";
            public static string NotEnoughLives(int cost) =>
                $"Not enough lives to self-revive (Costs {cost})";
        }

        //====================[ Revive Complete ]====================
        internal static class ReviveComplete
        {
            public const string LocalSelf = "Revive Item used successfully! You are temporarily invulnerable.";
            public const string LocalTeam = "Revived by teammate! You are temporarily invulnerable.";

            public static string ObserverBySource(string playerDisplay, ReviveSource source)
            {
                return source == ReviveSource.Self
                    ? $"{playerDisplay} self-revived"
                    : $"{playerDisplay} was revived";
            }
        }

        //====================[ Revive Denials ]====================
        internal static class ReviveDenied
        {
            public const string AuthorizationFailed = "Revive authorization failed";
            public const string TeamFallback = "Revive denied";
            public const string SelfFallback = "Revive denied by server";
            public const string Cooldown = "Player on cooldown";
            public const string OutOfLives = "Out of lives - can't be revived again";
        }

        //====================[ Network Revive ]====================
        internal static class NetworkRevive
        {
            public static string TeamHelpedBy(string reviverName, string reviveeName) =>
                $"{reviverName} is helping {reviveeName}";
            public static string TeamCancelledBy(string reviverName) =>
                $"{reviverName} cancelled revival";

            public static string SelfReviving(string display) =>
                $"{display} is self-reviving";

            public static string TeamReviving(string reviverName, string reviveeName) =>
                $"{reviverName} is reviving {reviveeName}";
        }

        //====================[ Downed UI ]====================
        internal static class Downed
        {
            public const string DownedBanner = "DOWNED";
            public const string ReviverTimedOut = "Reviver disconnected or timed out.";
            public const string GiveUpCanceled = "Give up canceled";

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
        internal static class Death
        {
            public const string HeadshotCritical = "Headshot – critical";
            public const string HeadshotKilled = "Headshot – killed instantly";
            public const string YouDied = "You died";
        }

        //====================[ Spectate ]====================
        internal static class Spectate
        {
            public const string NoTeammates = "No teammates left to spectate";
        }

        //====================[ Ghost Mode ]====================
        internal static class GhostMode
        {
            public static string State(bool entered) =>
                $"GhostMode: {(entered ? "Entered (F7)" : "Exited (F8)")}";
        }
    }
}
