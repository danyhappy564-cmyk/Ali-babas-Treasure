using SPTarkov.Server.Core.Models.Spt.Mod;

namespace AlibabaTreasure.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.rf.alibabatreasure";
    public string Name { get; init; } = "Ali Baba's Treasure";
    public string Author { get; init; } = "R_F";
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; }

    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/danyhappy564-cmyk/Ali-babas-Treasure";
}
