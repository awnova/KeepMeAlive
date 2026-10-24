//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Utils;

namespace KeepMeAlive.Animation.Hands.DefibHands
{
    //====================[ DefibHands ]====================
    // The reviver's defibrillator sequence for a team revive (DefibHandsDriver). It plays on plain empty hands: the reviver
    // stows what they hold the way the game does for any hands change (SetEmptyHands, which Fika sends to everyone), the
    // arms are posed by the mod (ArmPoser) and the fingers and paddles are put on the hands after it, and the
    // weapon comes back at the end the way the game does it (TrySetLastEquippedWeapon). Nothing is put in the hands, so
    // there is no item, controller or rig to swap in and no hands change to copy to the other players: they stow and draw
    // the reviver's weapon from the same packets, and each of them plays the sequence from the TeamHelp packet (OnTeamCancel
    // ends it early). Runs on every peer.
    internal static class DefibHands
    {
        private sealed class Session
        {
            public Player Player;
            public DefibHandsDriver Driver;
            // This peer made the hands empty, and so gives the weapon back.
            public bool StowedWeapon;
        }

        private static readonly Dictionary<string, Session> Sessions = new Dictionary<string, Session>();

        //====================[ Loading ]====================
        // Reads the track and builds the paddles at raid start, so the first revive does not hitch.
        public static void Preload()
        {
            _ = DefibHandsTrack.Instance;
            DefibPaddles.LoadAssets();
        }

        // Raid boundary: the players go with the raid.
        public static void Clear()
        {
            foreach (var session in Sessions.Values) session.Driver?.Dispose();
            Sessions.Clear();
            DefibPaddles.ClearMaterial();
        }

        //====================[ Entry Points ]====================
        // Starts the sequence for a hold of holdSeconds. False when it can't: no defibrillator item, or the game is already
        // changing the player's hands; the revive then goes on without it.
        public static bool Begin(Player player, float holdSeconds)
        {
            // A headless host draws nothing.
            if (FikaBackendUtils.IsHeadless) return false;
            if (player == null || player.HealthController == null || !player.HealthController.IsAlive) return false;
            if (Sessions.ContainsKey(player.ProfileId))
            {
                HandTrace.Log($"defib {player.ProfileId}: Begin refused, a sequence is already running");
                return false;
            }
            if (player.ProcessStatus != Player.EProcessStatus.None)
            {
                HandTrace.Log($"defib {player.ProfileId}: Begin refused, the game is changing the hands (process status {player.ProcessStatus})");
                return false;
            }

            bool hasItem = ModUtils.FindDefib(player) != null;
            var driver = hasItem ? DefibHandsDriver.Create(player, holdSeconds) : null;
            if (driver == null)
            {
                HandTrace.Log($"defib {player.ProfileId}: Begin refused ({(hasItem ? "the driver could not be built: track, camera or a bone is missing" : "no defibrillator item found on the player")})");
                return false;
            }

            var session = new Session { Player = player, Driver = driver };
            Sessions[player.ProfileId] = session;
            // The first-person arms grow back to full size while the weapon is stowed (DefibHandsDriver waits for it).
            FullScaleArms.Want(player, driver, true);

            // Only the reviver's own peer changes their hands; the others get the change from Fika.
            if (player.IsYourPlayer && !ModUtils.IsEmptyHanded(player))
            {
                player.TrySaveLastItemInHands();
                player.SetEmptyHands(null);
                session.StowedWeapon = true;
            }
            RevivalDebugLog.LogDebug($"[DefibHands] {player.ProfileId}: sequence for {holdSeconds:F1}s (local={player.IsYourPlayer}, stowed={session.StowedWeapon})");
            HandTrace.Log($"defib {player.ProfileId}: Begin ok, hold {holdSeconds:F1}s, local={player.IsYourPlayer}, firstPerson={player.FirstPersonPointOfView}, handsNow={player.HandsController?.GetType().Name}, stowed={session.StowedWeapon}");
            return true;
        }

        // The hold is over. Cancelled: the hands go back at once. Completed: the sequence ends on its own (the shock lands
        // on the hold's end, then the put-away) and the weapon comes back after it.
        public static void End(Player player, bool completed)
        {
            if (player == null || !Sessions.TryGetValue(player.ProfileId, out var session)) return;
            if (completed) return;

            session.Driver.Cancel();
        }

        // Once per frame, straight after the player's own late pass (BodyAnimation.AfterPlayerPose).
        public static void AfterPlayerPose(Player player)
        {
            if (Sessions.Count == 0 || player == null) return;
            if (!Sessions.TryGetValue(player.ProfileId, out var session) || session.Player != player) return;

            bool alive = player.HealthController != null && player.HealthController.IsAlive;
            session.Driver.Tick(alive && ModUtils.IsEmptyHanded(player));
            if (session.Driver.IsDone || !alive) Finish(session);
        }

        //====================[ Ending ]====================
        private static void Finish(Session session)
        {
            var player = session.Player;
            Sessions.Remove(player.ProfileId);
            session.Driver.Dispose();
            RevivalDebugLog.LogDebug($"[DefibHands] {player.ProfileId}: sequence over (stowed={session.StowedWeapon})");
            HandTrace.Log($"defib {player.ProfileId}: sequence over (stowed={session.StowedWeapon}, handsNow={player.HandsController?.GetType().Name}, processStatus={player.ProcessStatus})");

            if (!session.StowedWeapon) return;
            // Downed or dead: the downed flow (or death) deals with the hands.
            if (player.HealthController == null || !player.HealthController.IsAlive || RMSession.IsPlayerCritical(player.ProfileId)) return;
            // Still empty (or still being made empty): nobody else has taken the hands, so the weapon goes back.
            if (ModUtils.IsEmptyHanded(player) || player.ProcessStatus != Player.EProcessStatus.None) player.TrySetLastEquippedWeapon();
        }
    }
}
