using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HarmonyProfiles.Server;

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
            if (!Ready) throw new InvalidOperationException("Harmony-Profiles has not finished loading.");
            string people = File.ReadAllText(EditorPath("personalities"));
            string simulation = File.ReadAllText(EditorPath("simulation"));
            string config = File.ReadAllText(EditorPath("settings"));
            return new(people, Revision(people), simulation, Revision(simulation), config, Revision(config),
                population.Careers.Count, population.Careers.Values.Count(c => c.IsOnline),
                encounters.Values.Count(e => e.StoryOutcome != "None" && e.KillerCapturedUtc > 0 && !population.AppliedStories.ContainsKey(e.AccountId)),
                population.Careers.Values.Count(c => c.PrestigeLevel > 0), GenerationMode, ApbsMatched, ApbsFallback);
        }
    }

    public void SaveEditorSection(string section, string contents, string revision)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("Harmony-Profiles is unavailable.");
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
                    var config = JsonSerializer.Deserialize<HarmonyProfilesSettings>(contents, options)
                        ?? throw new InvalidDataException("Settings are empty.");
                    config.Validate();

                    AtomicFile.Write(path, JsonSerializer.Serialize(config, options));
                    settings = config;
                    break;
            }
            logger.Info("[Harmony-Profiles] Web editor saved and applied section: " + section);
        }
    }
}
