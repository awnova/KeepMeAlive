//====================[ Imports ]====================
using KeepMeAlive.Server.Callbacks;
using KeepMeAlive.Server.Models.Revival;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace KeepMeAlive.Server.Routers.Static;

//====================[ RevivalStateStaticRouter ]====================
// SPT handles zlib decompression, session resolution, typed deserialization and response
// compression for these routes.
[Injectable]
public class RevivalStateStaticRouter(RevivalStateCallbacks callbacks, JsonUtil jsonUtil) : StaticRouter(jsonUtil, [
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/begin-critical",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.BeginCritical(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/request-revive-start",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.RequestReviveStart(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/cancel-revive-start",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.CancelReviveStart(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/complete-revive",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.CompleteRevive(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/end-invulnerability",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.EndInvulnerability(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/reset",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.Reset(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/get",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.Get(url, info, sessionId)),
        new RouteAction<RevivalAuthorityRequest>("/keepmealive/state/get-runtime-config",
            async (url, info, sessionId, output, cancellationToken) => await callbacks.GetRuntimeConfig(url, info, sessionId)),
    ])
{
}
