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
    private readonly Dictionary<string, MongoId> gearRequests = new();
    private sealed record GearRequest(Career Career, MongoId Session, long Order, long UpdatedUtc);

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
        bool initial = career.GearVersion == 0 && career.LastEquipmentAccount == 0 && !career.NeedsRegear;
        if (!initial && !(career.IsOnline && career.GearPending && !career.NeedsRegear)) return;
        // Generating here would hold the gate through APBS and stall bot spawns, so the viewer gets it on the next look.
        gearRequests[career.Id] = session;
        _ = Task.Run(DrainGearRequests);
    }

    private bool GearBlocked(Career career, long now, bool story) => career.Retired || IsObservedActive(career, now)
        || (!story && (now < career.StoryHoldUntilUtc || now - career.LastEquipmentGeneratedUtc < 900));

    private bool GenerateCareerGear(Career career, MongoId session, long now, bool story = false)
    {
        if (gearFactory is null || now < career.NextGearAttemptUtc || GearBlocked(career, now, story)) return false;
        career.NextGearAttemptUtc = now + 60;
        try
        {
            ApplyGear(career, GenerateIsolatedEquipment(career, session), now);
            return true;
        }
        catch (Exception ex)
        {
            GearFailed(career, ex);
            return false;
        }
    }

    private void ApplyGear(Career career, JsonObject equipment, long now)
    {
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
    }

    private void GearFailed(Career career, Exception ex)
    {
        if (gearFailures.Add(career.Id)) logger.Warning($"[Harmony-Profiles] APBS profile gear deferred for account {career.AccountId}; existing equipment retained. {ex.GetBaseException().Message}");
    }

    // Caller holds the gate. Claims up to three requests so a backlog cannot stretch one checkpoint.
    private List<GearRequest> TakeGearRequests(long now)
    {
        var result = new List<GearRequest>();
        if (gearFactory is null) return result;
        foreach (var (id, session) in gearRequests.Take(3).ToArray())
        {
            gearRequests.Remove(id);
            if (!Ready || !population.Careers.TryGetValue(id, out var career)
                || now < career.NextGearAttemptUtc || GearBlocked(career, now, false)) continue;
            career.NextGearAttemptUtc = now + 60;
            result.Add(new(career, session, career.LastEquipmentOrder, career.LastGearUpdateUtc));
        }
        return result;
    }

    private void DrainGearRequests()
    {
        try
        {
            List<GearRequest> work;
            lock (gate) work = TakeGearRequests(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var request in work) GenerateRequestedGear(request);
        }
        catch (Exception ex) { Report(ex); }
    }

    // Runs without the gate. APBS serializes itself through ApbsGearFactory's own lock.
    private void GenerateRequestedGear(GearRequest request)
    {
        JsonObject equipment;
        try { equipment = GenerateIsolatedEquipment(request.Career, request.Session); }
        catch (Exception ex)
        {
            lock (gate) GearFailed(request.Career, ex);
            return;
        }
        lock (gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // An encounter may have replaced the loadout while APBS was running. Keep the newer one.
            if (!Ready || request.Career.LastEquipmentOrder != request.Order || request.Career.LastGearUpdateUtc != request.UpdatedUtc
                || GearBlocked(request.Career, now, false)) return;
            try { ApplyGear(request.Career, equipment, now); }
            catch (Exception ex) { GearFailed(request.Career, ex); }
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
        gearRequests.TryAdd(career.Id, session);
        return true;
    }
}
