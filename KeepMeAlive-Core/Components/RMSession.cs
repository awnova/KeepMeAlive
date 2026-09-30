//====================[ Imports ]====================
using System;
using System.Collections.Generic;

namespace KeepMeAlive.Components
{
    //====================[ RMSession ]====================
    // Per-raid store of revival state for every tracked human player (local + remote).
    // Main-thread only. Cleared on raid start and raid end via Clear().
    internal static class RMSession
    {
        public static event Action<string, RMState, RMState> PlayerStateChanged;

        private static readonly Dictionary<string, RMPlayer> PlayerStates = new();

        public static void Clear() => PlayerStates.Clear();

        public static bool IsPlayerCritical(string playerId) =>
            TryGetPlayerState(playerId, out var st) && st.IsCritical;

        public static RMPlayer GetPlayerState(string playerId)
        {
            if (!PlayerStates.TryGetValue(playerId, out var state))
            {
                state = new RMPlayer();
                PlayerStates[playerId] = state;
            }
            return state;
        }

        public static bool TryGetPlayerState(string playerId, out RMPlayer state)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                state = null;
                return false;
            }
            return PlayerStates.TryGetValue(playerId, out state);
        }

        // Drops a single player's cached state when their remote Player object is removed.
        public static void RemovePlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (!PlayerStates.TryGetValue(playerId, out var state)) return;

            var oldState = state.State;
            PlayerStates.Remove(playerId);

            if (oldState == RMState.None) return;

            try
            {
                PlayerStateChanged?.Invoke(playerId, oldState, RMState.None);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"RMSession.RemovePlayer event error: {ex.Message}");
            }
        }

        public static bool SetPlayerState(string playerId, RMState newState)
        {
            if (string.IsNullOrEmpty(playerId)) return false;

            var state = GetPlayerState(playerId);
            var oldState = state.State;
            if (oldState == newState) return false;

            state.State = newState;

            try
            {
                PlayerStateChanged?.Invoke(playerId, oldState, newState);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"RMSession.SetPlayerState event error: {ex.Message}");
            }

            return true;
        }
    }
}
