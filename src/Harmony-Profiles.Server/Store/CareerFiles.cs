using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Path = System.IO.Path;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    // Only this many careers keep their equipment, showcase and stash in memory at once.
    private const int AssetCacheSize = 256;
    private readonly Dictionary<string, LinkedListNode<CareerAssets>> assetCache = new();
    private readonly LinkedList<CareerAssets> assetOrder = new();
    private readonly HashSet<string> assetFiles = new();
    private long assetSequence;
    private readonly object assetGate = new();
    private readonly Dictionary<string, long> assetsWritten = new();
    private sealed record AssetSnapshot(CareerAssets Assets, long Sequence, long Version, string Text);

    private string CareerDirectory => Path.Combine(directory, "careers");

    private void IndexCareerFiles()
    {
        Directory.CreateDirectory(CareerDirectory);
        foreach (string file in Directory.EnumerateFiles(CareerDirectory, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (population.Careers.ContainsKey(id)) assetFiles.Add(id);
        }
    }

    // Caller holds the gate. Don't hold the result across hundreds of other careers' loads.
    private CareerAssets Assets(Career c)
    {
        if (assetCache.TryGetValue(c.Id, out var node))
        {
            assetOrder.Remove(node);
            assetOrder.AddFirst(node);
            return node.Value;
        }
        var assets = assetFiles.Contains(c.Id) ? ReadAssets(c) : null;
        assets ??= new CareerAssets { CareerId = c.Id, Equipment = EmptyEquipment(StableId(c.Id + ":equipment")) };
        assetCache.Add(c.Id, assetOrder.AddFirst(assets));
        while (assetOrder.Count > AssetCacheSize)
        {
            var last = assetOrder.Last!;
            if (last.Value.Version > last.Value.SavedVersion)
            {
                try
                {
                    var snapshot = SnapshotAssets(last.Value);
                    WriteAssets(snapshot);
                    Saved(snapshot);
                }
                catch (Exception ex)
                {
                    // Keep it cached so the next checkpoint retries instead of dropping the change.
                    logger.Error($"[Harmony-Profiles] Career file save failed; retaining it in memory. {ex.Message}");
                    break;
                }
            }
            assetOrder.RemoveLast();
            assetCache.Remove(last.Value.CareerId);
        }
        return assets;
    }

    private CareerAssets? ReadAssets(Career c)
    {
        string path = Path.Combine(CareerDirectory, c.Id + ".json");
        try
        {
            var assets = JsonSerializer.Deserialize<CareerAssets>(File.ReadAllText(path));
            if (assets is null || assets.SchemaVersion != 1 || assets.CareerId != c.Id || assets.Equipment is null
                || assets.ShowcaseItems is null || assets.ShowcaseItems.Count > 4096)
                throw new InvalidDataException("Invalid career file.");
            return assets;
        }
        catch (Exception ex)
        {
            // A fictional loadout is not worth disabling the mod over. The next change rewrites the file.
            logger.Warning($"[Harmony-Profiles] Career file for account {c.AccountId} unreadable; using empty gear. {ex.Message}");
            return null;
        }
    }

    // Caller holds the gate, so a higher sequence is always the newer content.
    private AssetSnapshot SnapshotAssets(CareerAssets assets)
    {
        assetFiles.Add(assets.CareerId);
        return new(assets, ++assetSequence, assets.Version, JsonSerializer.Serialize(assets));
    }

    private void WriteAssets(AssetSnapshot snapshot)
    {
        lock (assetGate)
        {
            string id = snapshot.Assets.CareerId;
            if (assetsWritten.GetValueOrDefault(id) >= snapshot.Sequence) return;
            AtomicFile.Write(Path.Combine(CareerDirectory, id + ".json"), snapshot.Text, durable: false, backup: false);
            assetsWritten[id] = snapshot.Sequence;
        }
    }

    // Caller holds the gate.
    private static void Saved(AssetSnapshot snapshot) =>
        snapshot.Assets.SavedVersion = Math.Max(snapshot.Assets.SavedVersion, snapshot.Version);

    // Caller holds the gate. The writes can happen after it is released.
    private List<AssetSnapshot> SnapshotChangedAssets() =>
        assetOrder.Where(a => a.Version > a.SavedVersion).Select(SnapshotAssets).ToList();

    private void SaveChangedAssets()
    {
        foreach (var snapshot in SnapshotChangedAssets())
        {
            WriteAssets(snapshot);
            Saved(snapshot);
        }
    }

    private JsonObject CareerEquipment(Career c) => Assets(c).Equipment;

    private void SetEquipment(Career c, JsonObject equipment)
    {
        var assets = Assets(c);
        assets.Equipment = equipment;
        assets.Changed();
        c.VisibleLoadout = HasVisibleLoadout(equipment);
    }

    // Encounter files are written before they are trimmed, so the file always has the equipment.
    private JsonObject EncounterEquipment(Encounter e)
    {
        if (!e.Trimmed) return e.Equipment;
        string path = Path.Combine(directory, "encounters", e.AccountId + ".json");
        return JsonSerializer.Deserialize<Encounter>(File.ReadAllText(path))?.Equipment
            ?? throw new InvalidDataException($"Empty encounter {path}");
    }

    private static void TrimEncounter(Encounter e)
    {
        e.Equipment = new();
        e.Trimmed = true;
    }

    // Item and customization removals are the only reason to repair stored gear, so skip it when neither changed.
    private string TemplateFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string id in templates.Items.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal))
            hash.AppendData(Encoding.ASCII.GetBytes(id));
        hash.AppendData([0]);
        foreach (string id in templates.Customization.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal))
            hash.AppendData(Encoding.ASCII.GetBytes(id));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string StableId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
}
