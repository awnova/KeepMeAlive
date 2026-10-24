
namespace KeepMeAlive.Diagnostics
{
    //====================[ RevivalDebugLog ]====================
    internal static class RevivalDebugLog
    {
        //====================[ Flags ]====================
        // One server-side switch (Gameplay.Development.DebugMode); off until the server config has loaded.
        public static bool IsDebugLogsEnabled => SyncedServerConfigStore.Config.Gameplay.Development.DebugMode;

        public static bool IsReviveFlowEnabled => IsDebugLogsEnabled;

        public static bool IsNetworkTraceEnabled => IsDebugLogsEnabled;

        public static bool IsSelfReviveTraceEnabled => IsDebugLogsEnabled;

        //====================[ Logging API ]====================
        public static void LogDebug(string message)
        {
            if (!IsDebugLogsEnabled) return;
            Plugin.LogSource.LogDebug(message);
        }

        public static void LogReviveFlow(string message)
        {
            if (!IsReviveFlowEnabled) return;
            Plugin.LogSource.LogInfo(message);
        }

        public static void LogNetworkTrace(string message)
        {
            if (!IsNetworkTraceEnabled) return;
            Plugin.LogSource.LogInfo(message);
        }

        public static void LogSelfReviveTrace(string message)
        {
            if (!IsSelfReviveTraceEnabled) return;
            Plugin.LogSource.LogInfo(message);
        }

        // Shared formatter for revive-state transitions logged by packet handlers and controllers.
        public static void LogStateTrace(string source, string playerId, RMState from, RMState to, string details = "")
        {
            LogNetworkTrace($"[StateTrace:{source}] {playerId}: {from} -> {to} {details}");
        }
    }
}