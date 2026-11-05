//====================[ Imports ]====================
namespace KeepMeAlive.Diagnostics
{
    //====================[ ReviveDebug ]====================
    // Step-by-step revive-flow logging emitted through RevivalDebugLog.
    internal static class ReviveDebug
    {
        //====================[ Public API ]====================
        public static void Log(string step, string playerId, bool isLocal, string details = null)
        {
            if (!RevivalDebugLog.IsReviveFlowEnabled) return;

            string machine = FikaBridge.IsHeadless() ? "Headless" : "Client";
            string server = FikaBridge.IAmHost() ? "T" : "F";
            string local = isLocal ? "T" : "F";
            string tail = string.IsNullOrEmpty(details) ? string.Empty : $" {details}";
            RevivalDebugLog.LogReviveFlow($"[ReviveDebug:{step}] machine={machine} server={server} playerId={playerId} isLocal={local}{tail}");
        }
    }
}
