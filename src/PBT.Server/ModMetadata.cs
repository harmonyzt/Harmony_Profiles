using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace PBT.Server;

public record ModMetadata : IModMetadata, SPTarkov.Server.Web.IModBlazorMetadata
{
    public string? WWWRootUrl { get; init; } = "pbt-assets";
    public string? HomePage { get; init; } = "/pbt";
    public string? HomePageDescription { get; init; } = "Population, personalities, prestige, showcases, and stories";
    public string ModGuid { get; init; } = "com.local.pbt";
    public string Name { get; init; } = "PBT";
    public string Author { get; init; } = "Harmony";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("0.14.0");
    public Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; } = ["com.local.profiles"];
    public Dictionary<string, Range>? ModDependencies { get; init; } = new() { ["com.acidphantasm.progressivebotsystem"] = new(">=2.4.1 <3.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
