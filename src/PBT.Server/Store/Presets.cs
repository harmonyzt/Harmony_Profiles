using System.Text.Json;

namespace PBT.Server;

public sealed class PbtPreset
{
    // A missing section leaves that part of the live settings alone, so a shared preset can cover just one area.
    public List<PersonalityTemplate>? Personalities { get; set; }
    public SimulationPolicy? Simulation { get; set; }
    public PbtSettings? Settings { get; set; }
}

public sealed record PresetInfo(string Name, bool BuiltIn, bool Selected, bool Modified);

// Named sets of every editor setting, kept as .json files in the mod's Presets folder.
public sealed partial class PopulationStore
{
    public const string DefaultPreset = "Default";
    private static readonly JsonSerializerOptions PresetJson = new() { WriteIndented = true };

    private string PresetFolder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory)!, "Presets");
    private string SelectionPath => System.IO.Path.Combine(PresetFolder, "selected.txt");

    private static PbtPreset BuiltInPreset() => new() { Personalities = DefaultPersonalities(), Simulation = new(), Settings = new() };

    private static string CheckName(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > 40 || trimmed.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '-' or '_' or '.')) || trimmed.StartsWith('.'))
            throw new InvalidDataException("Preset names are 1-40 letters, digits, spaces, dashes, dots or underscores.");
        return trimmed;
    }

    private string PresetPath(string name) => System.IO.Path.Combine(PresetFolder, CheckName(name) + ".json");

    private PbtPreset ReadPreset(string name)
    {
        if (name.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase)) return BuiltInPreset();
        string path = PresetPath(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Preset {name} no longer exists.");
        var preset = JsonSerializer.Deserialize<PbtPreset>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Preset {name} is empty.");
        if (preset.Personalities is { } people) ValidatePersonalities(people);
        preset.Simulation?.Validate();
        preset.Settings?.Validate();
        return preset;
    }

    private string SelectedPreset()
    {
        try { return File.Exists(SelectionPath) ? CheckName(File.ReadAllText(SelectionPath)) : DefaultPreset; }
        catch { return DefaultPreset; }
    }

    private bool MatchesLive(PbtPreset preset) =>
        (preset.Personalities is null || JsonSerializer.Serialize(preset.Personalities) == JsonSerializer.Serialize(personalities))
        && (preset.Simulation is null || JsonSerializer.Serialize(preset.Simulation) == JsonSerializer.Serialize(policy))
        && (preset.Settings is null || JsonSerializer.Serialize(preset.Settings) == JsonSerializer.Serialize(settings));

    public List<PresetInfo> GetPresets()
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
            Directory.CreateDirectory(PresetFolder);
            string selected = SelectedPreset();
            var names = Directory.EnumerateFiles(PresetFolder, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && !n.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase))
                .Select(n => n!).Order(StringComparer.OrdinalIgnoreCase).Prepend(DefaultPreset);
            var list = new List<PresetInfo>();
            foreach (string name in names)
            {
                bool isSelected = name.Equals(selected, StringComparison.OrdinalIgnoreCase);
                bool modified = false;
                // A broken file still lists, so it can be deleted from the editor.
                if (isSelected) try { modified = !MatchesLive(ReadPreset(name)); } catch { modified = true; }
                list.Add(new(name, name == DefaultPreset, isSelected, modified));
            }
            return list;
        }
    }

    public void ApplyPreset(string name)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT is unavailable.");
            var preset = ReadPreset(name);
            WriteLive(preset);
            Select(name);
            logger.Info("[PBT] Loaded preset " + name);
        }
    }

    // Saves the editor's values as the live settings and as a preset, then selects it.
    public void SavePreset(string name, bool overwrite, string personalitiesJson, string simulationJson, string settingsJson, string[] revisions)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT is unavailable.");
            name = CheckName(name);
            if (name.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Default is built in. Save under a new name.");
            string path = PresetPath(name);
            if (!overwrite && File.Exists(path)) throw new InvalidOperationException($"A preset called {name} already exists.");
            string[] sections = ["personalities", "simulation", "settings"];
            for (int i = 0; i < sections.Length; i++)
                if (Revision(File.ReadAllText(EditorPath(sections[i]))) != revisions[i])
                    throw new InvalidOperationException("Settings changed since you opened them. Reload saved settings before saving.");
            var strict = new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            var preset = new PbtPreset
            {
                Personalities = JsonSerializer.Deserialize<List<PersonalityTemplate>>(personalitiesJson, strict),
                Simulation = JsonSerializer.Deserialize<SimulationPolicy>(simulationJson, strict),
                Settings = JsonSerializer.Deserialize<PbtSettings>(settingsJson, strict)
            };
            if (preset.Personalities is null || preset.Simulation is null || preset.Settings is null)
                throw new InvalidDataException("Settings are empty.");
            foreach (var item in preset.Personalities) if (string.IsNullOrWhiteSpace(item.ProgressTo)) item.ProgressTo = null;
            WriteLive(preset);
            Directory.CreateDirectory(PresetFolder);
            AtomicFile.Write(path, JsonSerializer.Serialize(preset, PresetJson), backup: false);
            Select(name);
            logger.Info("[PBT] Saved preset " + name);
        }
    }

    public void DeletePreset(string name)
    {
        lock (gate)
        {
            name = CheckName(name);
            if (name.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Default is built in and cannot be deleted.");
            File.Delete(PresetPath(name));
            if (SelectedPreset().Equals(name, StringComparison.OrdinalIgnoreCase)) Select(DefaultPreset);
        }
    }

    // Caller holds the gate. Everything is validated before the first file changes.
    private void WriteLive(PbtPreset preset)
    {
        if (preset.Personalities is { } people) ValidatePersonalities(people);
        preset.Simulation?.Validate();
        preset.Settings?.Validate();
        if (preset.Personalities is not null)
        {
            AtomicFile.Write(EditorPath("personalities"), JsonSerializer.Serialize(preset.Personalities, PresetJson));
            personalities = preset.Personalities;
        }
        if (preset.Simulation is not null)
        {
            AtomicFile.Write(EditorPath("simulation"), JsonSerializer.Serialize(preset.Simulation, PresetJson));
            policy = preset.Simulation;
        }
        if (preset.Settings is not null)
        {
            AtomicFile.Write(EditorPath("settings"), JsonSerializer.Serialize(preset.Settings, PresetJson));
            settings = preset.Settings;
        }
    }

    private void Select(string name)
    {
        Directory.CreateDirectory(PresetFolder);
        AtomicFile.Write(SelectionPath, name, backup: false);
    }
}
