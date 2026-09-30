//====================[ Imports ]====================
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace KeepMeAlive.Server;

//====================[ ServerModMetadata ]====================
// SPT 4 C# server mod metadata.
public record ServerModMetadata : IModMetadata
{
    public string Name { get; init; } = "KeepMeAlive Server";
    public string Author { get; init; } = "awnova";
    public List<string>? Contributors { get; init; } = ["KaikiNoodles - original mod this project is based on", "thuynguyentrungdang - original mod this project is based on"];
    public List<string>? Incompatibilities { get; init; } = [];
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = [];
    public string? Url { get; init; } = "https://github.com/awnova/KeepMeAlive";
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
    public string ModGuid { get; init; } = "KeepMeAliveServer";
    public SemanticVersioning.Version Version { get; init; } = new(1, 0, 0);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
}
