//====================[ Imports ]====================
using System;
using EFT;

namespace KeepMeAlive.Helpers
{
    //====================[ GodMode ]====================
    internal static class GodMode
    {
        //====================[ Queries ]====================
        public static bool IsEnabled() => SyncedServerConfigStore.Config.Gameplay.Protection.EnableGodMode;

        //====================[ State Controls ]====================
        // Config-gated enable
        public static void Enable(Player player)
        {
            if (!IsEnabled() || player is null)
            {
                return;
            }
            
            ForceEnable(player);
        }

        // Always on enable
        public static void ForceEnable(Player player)
        {
            if (player is null)
            {
                return;
            }

            try
            {
                var hc = player.ActiveHealthController;
                if (hc == null)
                {
                    return;
                }

                hc.SetDamageCoeff(0f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GodMode] ForceEnable error: {ex.Message}");
            }
        }

        public static void Disable(Player player)
        {
            if (player is null)
            {
                return;
            }

            try
            {
                var hc = player.ActiveHealthController;
                if (hc == null)
                {
                    return;
                }

                hc.SetDamageCoeff(1f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GodMode] Disable error: {ex.Message}");
            }
        }
    }
}