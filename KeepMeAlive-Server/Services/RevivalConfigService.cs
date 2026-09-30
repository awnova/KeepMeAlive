//====================[ Imports ]====================
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Utils;

namespace KeepMeAlive.Server.Services;

//====================[ RevivalConfigService ]====================
[Injectable(InjectionType.Singleton)]
public class RevivalConfigService(ModHelper modHelper, JsonUtil jsonUtil, ISptLogger<RevivalConfigService> logger)
{
    //====================[ State ]====================
    // Loaded and normalized once at pre-load, then served to clients.
    public RevivalServerConfig Config { get; private set; } = new();

    public string ModPath => modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
    public string ConfigPath => Path.Combine(ModPath, "config.json");

    //====================[ Lifecycle ]====================
    public async Task OnPreLoadAsync()
    {
        string? originalJson = File.Exists(ConfigPath) ? await File.ReadAllTextAsync(ConfigPath) : null;

        RevivalServerConfig? loaded = null;
        bool parseFailed = false;
        if (originalJson != null)
        {
            try
            {
                loaded = jsonUtil.Deserialize<RevivalServerConfig>(originalJson);
            }
            catch (Exception ex)
            {
                parseFailed = true;
                logger.Error($"[KeepMeAlive.Server] config.json is not valid JSON ({ex.Message}). Using defaults for this session; fix or delete the file to regenerate it.");
            }
        }

        Config = loaded ?? new RevivalServerConfig();
        Config.Normalize();

        // Write the file when it is missing or normalization adds or changes fields.
        string normalizedJson = jsonUtil.Serialize(Config, true) ?? string.Empty;
        if (!parseFailed && !string.Equals(originalJson?.Trim(), normalizedJson.Trim(), StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(ConfigPath, normalizedJson);
        }
    }
}
