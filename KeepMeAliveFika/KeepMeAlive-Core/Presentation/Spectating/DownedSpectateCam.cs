//====================[ Imports ]====================
using UnityEngine;

namespace KeepMeAlive.Presentation.Spectating
{
    //====================[ DownedSpectateCam ]====================
    // Lives on the player camera while a downed player spectates. It owns nothing: DownedSpectate decides what to
    // follow and puts everything back. The camera is not parented; the pose is written twice a frame with the same
    // function, last in LateUpdate (after the game's own late passes and the target's animation) and again in OnPreCull
    // (after any other late writer), so whatever reads the camera afterwards sees the final pose.
    [DefaultExecutionOrder(32000)]
    internal sealed class DownedSpectateCam : MonoBehaviour
    {
        private void Update() => DownedSpectate.HandleInput();

        private void LateUpdate()
        {
            DownedSpectate.Frame();
            Apply();
        }

        private void OnPreCull() => Apply();

        private void Apply()
        {
            // No target (or Fika has the camera): leave the camera where it last was.
            if (!DownedSpectate.TryGetPose(out Vector3 position, out Quaternion rotation)) return;
            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
