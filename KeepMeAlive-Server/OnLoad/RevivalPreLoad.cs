//====================[ Imports ]====================
using KeepMeAlive.Server.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace KeepMeAlive.Server.OnLoad;

//====================[ RevivalPreLoad ]====================
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload)]
public class RevivalPreLoad(ISptLogger<RevivalPreLoad> logger, RevivalConfigService configService) : IOnLoad
{
    //====================[ Lifecycle ]====================
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        await configService.OnPreLoadAsync();
        logger.Info("[KeepMeAlive.Server] Pre-load complete.");
    }
}
