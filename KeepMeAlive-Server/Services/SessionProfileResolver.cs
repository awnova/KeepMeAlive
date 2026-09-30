//====================[ Imports ]====================
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace KeepMeAlive.Server.Services;

//====================[ SessionProfileResolver ]====================
// Maps the caller's session (PHPSESSID cookie) to the profile ids it may act for. In a scav
// raid the in-raid Player.ProfileId is the scav profile, not the session/PMC id, so both count.
[Injectable(InjectionType.Singleton)]
public class SessionProfileResolver(SaveServer saveServer, ISptLogger<SessionProfileResolver> logger)
{
    public HashSet<string> GetOwnedProfileIds(MongoId sessionId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { sessionId.ToString() };

        try
        {
            if (!saveServer.ProfileExists(sessionId)) return ids;

            var characters = saveServer.GetProfile(sessionId)?.CharacterData;
            var pmcId = characters?.PmcData?.Id;
            var scavId = characters?.ScavData?.Id;
            if (pmcId.HasValue) ids.Add(pmcId.Value.ToString());
            if (scavId.HasValue) ids.Add(scavId.Value.ToString());
        }
        catch (Exception ex)
        {
            logger.Warning($"[KeepMeAlive.Server] Could not resolve profiles for session {sessionId}: {ex.Message}");
        }

        return ids;
    }
}
