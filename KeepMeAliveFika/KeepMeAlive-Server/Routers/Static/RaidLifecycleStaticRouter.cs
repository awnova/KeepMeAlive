//====================[ Imports ]====================
using KeepMeAlive.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace KeepMeAlive.Server.Routers.Static;

//====================[ RaidLifecycleStaticRouter ]====================
// Hooks SPT's two local-raid lifecycle routes alongside SPT's own MatchStaticRouter:
//   /client/match/local/start - every Fika peer calls this for its own session before loading
//   /client/match/local/end   - every Fika peer submits its own raid result
// Revival state and lives are reset server-side for the calling session on both routes.
// HttpRouter runs every router matching a URL; registering after SPT's routers (Routers + 1) and
// returning `output` untouched passes SPT's response through. No body type is declared, so SPT
// skips a second deserialization of the request body for this router.
[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public class RaidLifecycleStaticRouter(
    RevivalStateService stateService,
    SessionProfileResolver profileResolver,
    JsonUtil jsonUtil) : StaticRouter(jsonUtil, [
        new RouteAction("/client/match/local/start",
            (url, info, sessionId, output, cancellationToken) => Reset(stateService, profileResolver, sessionId, output)),
        new RouteAction("/client/match/local/end",
            (url, info, sessionId, output, cancellationToken) => Reset(stateService, profileResolver, sessionId, output)),
    ])
{
    private static ValueTask<object> Reset(RevivalStateService stateService, SessionProfileResolver profileResolver,
        MongoId sessionId, string? output)
    {
        stateService.ResetForRaid(profileResolver.GetOwnedProfileIds(sessionId));
        return ValueTask.FromResult<object>(output ?? string.Empty);
    }
}
