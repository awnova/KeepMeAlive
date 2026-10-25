//====================[ Imports ]====================
using System;
using UnityEngine;

namespace KeepMeAlive.Presentation.Messaging
{
    //====================[ MessageAudience ]====================
    internal enum MessageAudience
    {
        LocalPlayer,
        InvolvedPlayers
    }

    //====================[ PlayerMessageRouter ]====================
    internal static class PlayerMessageRouter
    {
        public static void Notify(Color color, string message, MessageAudience audience, string actorPlayerId = null, string targetPlayerId = null)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            switch (audience)
            {
                case MessageAudience.LocalPlayer:
                    VFX_UI.Text(color, message);
                    break;

                case MessageAudience.InvolvedPlayers:
                    string localId = PlayerUtils.GetYourPlayer()?.ProfileId;
                    if (string.IsNullOrEmpty(localId))
                    {
                        return;
                    }

                    bool isActor = string.Equals(localId, actorPlayerId, StringComparison.Ordinal);
                    bool isTarget = string.Equals(localId, targetPlayerId, StringComparison.Ordinal);
                    if (isActor || isTarget)
                    {
                        VFX_UI.Text(color, message);
                    }
                    break;
            }
        }

        public static void NotifySafe(string tag, Color color, Func<string> buildMessage, string actorPlayerId = null, string targetPlayerId = null, MessageAudience audience = MessageAudience.InvolvedPlayers)
        {
            try
            {
                Notify(color, buildMessage(), audience, actorPlayerId, targetPlayerId);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[VFX_UI] {tag} notify failed: {ex.Message}");
            }
        }
    }
}
