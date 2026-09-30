//====================[ Imports ]====================
using Fika.Core.Main.Utils;

namespace KeepMeAlive.Helpers
{
    //====================[ ReviveDebug ]====================
    // Step-by-step revive-flow logging emitted through RevivalDebugLog.
    internal static class ReviveDebug
    {
        //====================[ Public API ]====================
        public static void Log(string step, string playerId, bool isLocal, string details = null)
        {
            if (!RevivalDebugLog.IsReviveFlowEnabled) return;

            string machine = FikaBackendUtils.IsHeadless ? "Headless" : "Client";
            string server = FikaBackendUtils.IsServer ? "T" : "F";
            string local = isLocal ? "T" : "F";
            string tail = string.IsNullOrEmpty(details) ? string.Empty : $" {details}";
            RevivalDebugLog.LogReviveFlow($"[ReviveDebug:{step}] machine={machine} server={server} playerId={playerId} isLocal={local}{tail}");
        }
    }
}
