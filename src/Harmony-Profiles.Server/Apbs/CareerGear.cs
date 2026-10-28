using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private ApbsGearFactory? gearFactory;
    private MongoId? gearSession;
    private readonly HashSet<string> gearFailures = new();

    private void ConfigureRequiredApbs(Harmony owner)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            a.GetName().Name?.Contains("progressivebotsystem", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException("Harmony-Profiles requires Acid's Progressive Bot System.");
        gearFactory = new ApbsGearFactory(assembly, owner, json);
    }

    private static bool HasVisibleLoadout(JsonObject equipment)
    {
        string? root = equipment["Id"]?.ToString();
        return root is not null && equipment["Items"]?.AsArray().OfType<JsonObject>()
            .Any(item => item["parentId"]?.ToString() == root && item["slotId"]?.ToString() is not (null or "Scabbard" or "Dogtag")) == true;
    }

    private void SeedMissingLoadouts()
    {
        foreach (var career in population.Careers.Values.Where(c => !c.Retired))
        {
            if (career.LastEquipmentOrder == 0 && !HasVisibleLoadout(career.Equipment) && career.StoryHoldUntilUtc == 0)
                career.NeedsRegear = false;
            if (career.GearVersion == 0) career.GearPending = true;
        }
    }

    private void RefreshSimulatedLoadout(Career career, MongoId session)
    {
        gearSession = session;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool initial = career.GearVersion == 0 && career.LastEquipmentAccount == 0 && !career.NeedsRegear;
        if (initial || (career.IsOnline && career.GearPending && !career.NeedsRegear)) GenerateCareerGear(career, session, now);
    }

    private bool GenerateCareerGear(Career career, MongoId session, long now, bool story = false)
    {
        if (gearFactory is null || career.Retired || now < career.NextGearAttemptUtc || IsObservedActive(career, now)
            || (!story && (now < career.StoryHoldUntilUtc || now - career.LastEquipmentGeneratedUtc < 900))) return false;
        career.NextGearAttemptUtc = now + 60;
        try
        {
            var equipment = GenerateIsolatedEquipment(career, session);
            if (!ValidEquipment(equipment) || !HasVisibleLoadout(equipment)) throw new InvalidDataException("APBS did not produce a usable equipment tree.");
            career.Equipment = equipment;
            career.EquipmentIsSimulated = true;
            career.NeedsRegear = false;
            career.LastEquipmentAccount = 0;
            career.GearVersion = 1;
            career.GearPending = false;
            career.LastGearUpdateUtc = now;
            career.NextGearAttemptUtc = now + Random.Shared.Next(600, 1801);
            EnsureShowcase(career);
            dirty = true;
            return true;
        }
        catch (Exception ex)
        {
            if (gearFailures.Add(career.Id)) logger.Warning($"[Harmony-Profiles] APBS profile gear deferred for account {career.AccountId}; existing equipment retained. {ex.GetBaseException().Message}");
            return false;
        }
    }

    private JsonObject GenerateIsolatedEquipment(Career career, MongoId session)
    {
        if (gearFactory is null || !bots.Types.TryGetValue(career.Side.ToLowerInvariant(), out var source))
            throw new InvalidOperationException("APBS PMC equipment services are unavailable.");
        var isolated = json.Deserialize<BotType>(json.Serialize(source)!)
            ?? throw new InvalidDataException("Cannot clone PMC inventory template.");
        var inventory = gearFactory.Generate(inventoryGenerator, inventoryContainers, session, isolated, career, Level(career.Experience));
        var equipment = EquipmentTree(JsonNode.Parse(json.Serialize(inventory)!)!.AsObject());
        if (!ValidEquipment(equipment)) throw new InvalidDataException("APBS returned an invalid equipment tree.");
        return equipment;
    }

    private bool ProcessPendingGear(long now)
    {
        if (gearSession is not MongoId session) return false;
        var career = population.Careers.Values.Where(c => !c.Retired && c.IsOnline && c.GearPending
                && now >= c.NextGearAttemptUtc && now >= c.StoryHoldUntilUtc && now - c.LastEquipmentGeneratedUtc >= 900
                && !IsObservedActive(c, now)).OrderBy(c => c.LastGearUpdateUtc).ThenBy(c => c.AccountId).FirstOrDefault();
        if (career is null) return false;
        GenerateCareerGear(career, session, now);
        return true;
    }
}
