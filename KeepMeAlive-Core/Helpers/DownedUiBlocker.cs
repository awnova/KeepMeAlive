//====================[ Imports ]====================
using System;
using Comfort.Common;
using EFT.UI;
using UnityEngine;
using UnityEngine.UI;

namespace KeepMeAlive.Helpers
{
    //====================[ DownedUiBlocker ]====================
    internal static class DownedUiBlocker
    {
        //====================[ State ]====================
        private const string OverlayName = "KeepMeAlive_DownedUiBlocker";

        private static GameObject _root;
        private static Image _blockerImage;

        //====================[ Public API ]====================
        internal static bool IsBlocked => _root != null && _root.activeSelf;

        internal static void SetBlocked(bool blocked)
        {
            try
            {
                if (Plugin.IAmDedicatedClient)
                {
                    return;
                }

                if (!blocked)
                {
                    DisableOverlay();
                    return;
                }

                if (!EnsureOverlay())
                {
                    return;
                }

                _root.SetActive(true);
                _blockerImage.raycastTarget = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedUiBlocker] SetBlocked({blocked}) warn: {ex.Message}");
            }
        }

        //====================[ Private Helpers ]====================
        private static bool EnsureOverlay()
        {
            if (_root != null && _blockerImage != null)
            {
                return true;
            }

            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            if (gameUi == null)
            {
                return false;
            }

            _root = new GameObject(OverlayName, typeof(RectTransform));
            _root.transform.SetParent(gameUi.transform, false);

            var rect = _root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var blocker = new GameObject("Blocker", typeof(RectTransform), typeof(Image));
            blocker.transform.SetParent(_root.transform, false);
            var blockerRect = blocker.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.offsetMin = Vector2.zero;
            blockerRect.offsetMax = Vector2.zero;

            _blockerImage = blocker.GetComponent<Image>();
            _blockerImage.color = new Color(0f, 0f, 0f, 0f);
            _blockerImage.raycastTarget = true;

            _root.SetActive(false);
            return true;
        }

        //====================[ Cleanup ]====================
        private static void DisableOverlay()
        {
            if (_blockerImage != null) _blockerImage.raycastTarget = false;

            if (_root != null)
            {
                _root.SetActive(false);
            }
        }
    }
}
