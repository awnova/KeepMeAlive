using KeepMeAlive.Components;

namespace KeepMeAlive.Helpers
{
    //====================[ RevivalDebugLog ]====================
    internal static class RevivalDebugLog
    {
        //====================[ Flags ]====================
        public static bool IsDebugLogsEnabled => KeepMeAliveSettings.ENABLE_DEBUG_LOGS?.Value ?? false;

        public static bool IsReviveFlowEnabled => IsDebugLogsEnabled && (KeepMeAliveSettings.DEBUG_REVIVE_FLOW?.Value ?? false);

        public static bool IsNetworkTraceEnabled => IsDebugLogsEnabled && (KeepMeAliveSettings.DEBUG_NETWORK_TRACE?.Value ?? false);

        public static bool IsSelfReviveTraceEnabled => IsDebugLogsEnabled && (KeepMeAliveSettings.DEBUG_SELF_REVIVE_TRACE?.Value ?? false);

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