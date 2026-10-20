//====================[ Imports ]====================
using UnityEngine;
using EFT.Communications;

namespace KeepMeAlive.Presentation.HUD
{
    //====================[ VFX_UI ]====================
    // Notification toasts. The downed/revive bars live in Hud.
    public static class VFX_UI
    {
        public static void Text(Color color, string message) =>
            NotificationManager.DisplayMessageNotification(
                message, ENotificationDurationType.Long, ENotificationIconType.Default, color);
    }
}
