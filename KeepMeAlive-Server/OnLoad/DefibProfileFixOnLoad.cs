//====================[ Imports ]====================
using KeepMeAlive.Server.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace KeepMeAlive.Server.OnLoad;

//====================[ DefibProfileFixOnLoad ]====================
// After SaveCallbacks (which loads the profiles into memory).
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public class DefibProfileFixOnLoad(DefibProfileFixService fixService) : IOnLoad
{
    //====================[ Lifecycle ]====================
    public Task OnLoadAsync(CancellationToken cancellationToken) => fixService.FixAllProfilesAsync(cancellationToken);
}
