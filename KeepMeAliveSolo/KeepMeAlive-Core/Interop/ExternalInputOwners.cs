//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.Linq;

namespace KeepMeAlive.Interop
{
    //====================[ ExternalInputOwners ]====================
    // Other plugins' input modes (KeepMeAliveApi), and what counts as KeepMeAlive owning the local player's input.
    // KeepMeAlive always wins: each of its own modes calls ReleaseAll as it starts, and LocalBusyReason is the
    // backstop the other plugin polls. A registration lasts the whole game session, like the plugin; it is never
    // cleared at raid end.
    internal static class ExternalInputOwners
    {
        //====================[ State ]====================
        private static readonly Dictionary<string, Action<string>> Owners = new Dictionary<string, Action<string>>();

        //====================[ Registry ]====================
        public static void Register(string id, Action<string> release)
        {
            if (string.IsNullOrEmpty(id) || release == null) return;
            Owners[id] = release;
            Plugin.LogSource.LogInfo($"[Interop] input owner registered: {id}");
        }

        public static void Unregister(string id)
        {
            if (id != null) Owners.Remove(id);
        }

        // From each KeepMeAlive mode as it takes the local player's input. A copy, so a release may unregister.
        public static void ReleaseAll(string reason)
        {
            if (Owners.Count == 0) return;
            foreach (var owner in Owners.ToArray())
            {
                try
                {
                    owner.Value(reason);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"[Interop] {owner.Key} release failed: {ex.Message}");
                }
            }
        }

        //====================[ Busy ]====================
        // Every KeepMeAlive mode that takes the local player's keys or mouse. A new such mode is added here too.
        public static string LocalBusyReason()
        {
            var player = PlayerUtils.GetYourPlayer();
            if (player == null) return null;
            // BleedingOut or Reviving (RMPlayer.IsCritical): self-revive, stims and the revive animation are inside it.
            if (RMSession.IsPlayerCritical(player.ProfileId)) return "downed";
            if (RMSession.TryGetPlayerState(player.ProfileId, out var st) && st.IsSilentInventoryAnimActive) return "revive animation";
            return null;
        }
    }
}
