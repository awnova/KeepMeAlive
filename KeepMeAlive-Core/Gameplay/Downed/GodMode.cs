//====================[ Imports ]====================
using System;
using EFT;

namespace KeepMeAlive.Gameplay.Downed
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

                if (_coeffBefore == null && hc.DamageCoeff > 0f) _coeffBefore = hc.DamageCoeff;
                hc.SetDamageCoeff(0f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GodMode] ForceEnable error: {ex.Message}");
            }
        }

        // The local player's damage coefficient before god mode took it to 0, put back when it ends (another mod may
        // run it at something other than 1). Null while god mode is off, or when it was already 0 as god mode began.
        private static float? _coeffBefore;

        // Raid boundary: the last raid's health controller is gone.
        public static void Clear() => _coeffBefore = null;

        // EFT's DoContusion/DoStun/DoPain are no-ops while DamageCoeff is 0, so effects applied
        // under god mode must briefly lift it. The previous coefficient is always restored.
        public static void RunWithDamageEnabled(Player player, Action action)
        {
            var hc = player?.ActiveHealthController;
            if (hc == null || hc.DamageCoeff > 0f)
            {
                action();
                return;
            }

            float previous = hc.DamageCoeff;
            hc.SetDamageCoeff(1f);
            try { action(); }
            finally { hc.SetDamageCoeff(previous); }
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

                // Not enabled here (or it was already 0 when it was): leave the coefficient alone. Fika sets it to 0 itself
                // (extraction, its console god mode), and that is not this mod's to undo.
                if (_coeffBefore is float before) hc.SetDamageCoeff(before);
                _coeffBefore = null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GodMode] Disable error: {ex.Message}");
            }
        }
    }
}
