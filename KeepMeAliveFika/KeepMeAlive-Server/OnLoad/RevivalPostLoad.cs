//====================[ Imports ]====================
using KeepMeAlive.Server.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace KeepMeAlive.Server.OnLoad;

//====================[ RevivalPostLoad ]====================
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 1)]
public class RevivalPostLoad(ISptLogger<RevivalPostLoad> logger, RevivalDatabasePatchService databasePatchService) : IOnLoad
{
    //====================[ Lifecycle ]====================
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        databasePatchService.OnPostLoad();
        logger.Info("[KeepMeAlive.Server] Post-load complete.");
        return Task.CompletedTask;
    }
}
