using EFT.CameraControl;
//====================[ Imports ]====================
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using UnityEngine;

namespace KeepMeAlive.Utilities
{
    // Generic utilities (HTTP routes, main camera lookup) that do not
    //====================[ ModUtils ]====================
    // belong to a specific gameplay domain such as PlayerUtils, InventoryUtils, or HealthUtils.
    internal static class ModUtils
    {
        //====================[ Hands ]====================
        //====================[ Network / HTTP ]====================

        // I extract SPT's inner "data" field before deserializing the response into T.
        private class SptEnvelope<T>
        {
            public T data { get; set; }
        }

        // Blocking HTTP call - keep it off the Unity main thread during play (RevivalAuthority runs it on a background
        // queue). The one main-thread caller is SyncedServerConfigStore: at plugin load, and at raid start only when
        // that first fetch failed.
        // Returns the server response and propagates transport or parsing exceptions to the caller.
        public static T ServerRoute<T>(string url, object data = default)
        {
            string jsonReq = JsonConvert.SerializeObject(data);
            string jsonRes = RequestHandler.PostJson(url, jsonReq);

            var envelope = JsonConvert.DeserializeObject<SptEnvelope<T>>(jsonRes);
            if (envelope != null && envelope.data != null)
            {
                return envelope.data;
            }

            // Fallback: response was already unwrapped (plain JSON).
            return JsonConvert.DeserializeObject<T>(jsonRes);
        }

        public static bool TryGetMainCamera(out Camera camera)
        //====================[ Player Lookups ]====================
        //====================[ Defib ]====================
        //====================[ Stim ]====================
        {
            if (CameraManager.Exist)
            {
                camera = CameraManager.Instance.Camera;
                return camera != null;
            }

            camera = null;
        //====================[ Inventory State ]====================
            return false;
        }
        //====================[ Team Heal ]====================
    }
}
