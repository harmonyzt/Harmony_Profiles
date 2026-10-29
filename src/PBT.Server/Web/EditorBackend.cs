using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private string EditorPath(string section) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory)!, section switch
    {
        "personalities" => "personalities.json", "simulation" => "simulation.json", "settings" => "config.json",
        _ => throw new InvalidDataException("Unknown editor section.")
    });
    private static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public EditorSnapshot GetEditorSnapshot()
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
            string people = File.ReadAllText(EditorPath("personalities"));
            string simulation = File.ReadAllText(EditorPath("simulation"));
            string config = File.ReadAllText(EditorPath("settings"));
            return new(people, Revision(people), simulation, Revision(simulation), config, Revision(config),
                population.Careers.Count, population.Careers.Values.Count(c => c.IsOnline),
                encounters.Values.Count(e => e.StoryOutcome != "None" && e.KillerCapturedUtc > 0 && !population.AppliedStories.ContainsKey(e.AccountId)),
                population.Careers.Values.Count(c => c.PrestigeLevel > 0), GenerationMode, ApbsMatched, ApbsFallback, Insights());
        }
    }

    private EditorInsights Insights()
    {
        var active = population.Careers.Values.Where(c => !c.Retired).ToList();
        var bands = new int[8];
        foreach (var c in active) bands[Math.Clamp((Level(c.Experience) - 1) / 10, 0, 7)]++;
        long Deaths(Career c) => Math.Max(1, c.Raids - c.Survived - c.MissingRaids - c.AwolRaids - c.RunThroughRaids + c.Observed.Deaths + c.StoryDeaths);
        var top = active.OrderByDescending(c => c.PrestigeLevel).ThenByDescending(c => c.Experience).Take(8)
            .Select(c => new EditorCareerRow(c.Name, c.Side, c.Archetype, Level(c.Experience), c.PrestigeLevel, (int)(c.Raids + c.Observed.Raids),
                Math.Round((double)(c.PmcKills + c.Observed.PmcKills) / Deaths(c), 2), c.IsOnline)).ToList();
        var owners = active.Where(c => AreaLevel(c, 24) > 0).ToList();
        return new(bands, active.Count(c => c.Side.Equals("Bear", StringComparison.OrdinalIgnoreCase)),
            active.Count(c => c.Side.Equals("Usec", StringComparison.OrdinalIgnoreCase)),
            active.GroupBy(c => c.Archetype).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            top, owners.Count(c => c.ShowcaseAreas.ContainsValue(24)), owners.Count);
    }

    public void SaveEditorSection(string section, string contents, string revision)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT is unavailable.");
            if (contents.Length > 262144) throw new InvalidDataException("Settings are too large.");
            string path = EditorPath(section);
            if (Revision(File.ReadAllText(path)) != revision)
                throw new InvalidOperationException("This section changed since you opened it. Reload saved settings before saving.");
            var options = new JsonSerializerOptions { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            switch (section)
            {
                case "personalities":
                    var templates = JsonSerializer.Deserialize<List<PersonalityTemplate>>(contents, options)
                        ?? throw new InvalidDataException("Personality list is empty.");
                    ValidatePersonalities(templates);
                    AtomicFile.Write(path, JsonSerializer.Serialize(templates, options));
                    personalities = templates;
                    break;
                case "simulation":
                    var next = JsonSerializer.Deserialize<SimulationPolicy>(contents, options)
                        ?? throw new InvalidDataException("Simulation policy is empty.");
                    next.Validate();
                    AtomicFile.Write(path, JsonSerializer.Serialize(next, options));
                    policy = next;
                    break;
                case "settings":
                    var config = JsonSerializer.Deserialize<PbtSettings>(contents, options)
                        ?? throw new InvalidDataException("Settings are empty.");
                    config.Validate();

                    AtomicFile.Write(path, JsonSerializer.Serialize(config, options));
                    settings = config;
                    break;
            }
            logger.Info("[PBT] Web editor saved and applied section: " + section);
        }
    }
}
