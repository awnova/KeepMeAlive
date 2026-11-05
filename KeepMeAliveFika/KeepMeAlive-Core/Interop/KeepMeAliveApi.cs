//====================[ Imports ]====================
using System;

namespace KeepMeAlive.Interop
{
    //====================[ KeepMeAliveApi ]====================
    // The only part of KeepMeAlive other plugins may call. Bind by reflection (it is never referenced at compile time)
    // and check ApiVersion first. ApiVersion changes only when a member below is removed, renamed or changes meaning;
    // adding a member doesn't change it. Main thread only.
    public static class KeepMeAliveApi
    {
        public const int ApiVersion = 1;

        // Why KeepMeAlive owns the local player's input right now ("downed", "revive animation", ...), or null when it
        // doesn't. Cheap: safe to call every frame.
        public static string LocalBusyReason() => ExternalInputOwners.LocalBusyReason();

        // Another plugin's input mode (a wheel, a menu). KeepMeAlive calls release(reason) the moment it takes input
        // over; release must end the mode at once and must not throw. Registering an id again replaces it.
        public static void RegisterInputOwner(string id, Action<string> release) => ExternalInputOwners.Register(id, release);

        public static void UnregisterInputOwner(string id) => ExternalInputOwners.Unregister(id);
    }
}
