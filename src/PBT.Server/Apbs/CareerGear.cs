using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace PBT.Server;

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
            ?? throw new InvalidOperationException("PBT requires Acid's Progressive Bot System.");
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
            if (career.LastEquipmentOrder == 0 && !career.VisibleLoadout && career.StoryHoldUntilUtc == 0)
                career.NeedsRegear = false;
            if (career.GearVersion == 0) career.GearPending = true;
        }
    }

    // The refresh timer from the last kit must not hold back the regear after a death.
    private static void MarkDead(Career c, long now)
    {
        c.NeedsRegear = true;
        c.GearPending = true;
        c.DiedUtc = now;
        c.NextGearAttemptUtc = 0;
    }

    // A dead account stays naked for a while, then turns up in a new kit.
    private static long RegearDelay(Career c) => 1200 + (long)(Stable(c.Id + ":regear:" + c.DiedUtc) * 2400);

    private static bool WantsGear(Career c, long now) => !c.Retired
        && (c.GearVersion == 0 && c.LastEquipmentAccount == 0 || c.NeedsRegear && now - c.DiedUtc >= RegearDelay(c));

    // Gear used to be queued on the first look, so every never-viewed or long-dead profile opened naked.
    // APBS runs outside the gate so a profile view never stalls bot spawns.
    private void GearForView(int accountId, MongoId session)
    {
        GearRequest request;
        lock (gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!Ready || gearFactory is null) return;
            gearSession = session;
            if (!byAccount.TryGetValue(accountId, out var career) || !WantsGear(career, now)
                || now < career.NextGearAttemptUtc || GearBlocked(career, now, false)) return;
            career.NextGearAttemptUtc = now + 60;
            request = new(career, session, career.LastEquipmentOrder, career.LastGearUpdateUtc);
        }
        GenerateRequestedGear(request);
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
        SetEquipment(career, equipment);
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
        if (gearFailures.Add(career.Id)) logger.Warning($"[PBT] APBS profile gear deferred for account {career.AccountId}; existing equipment retained. {ex.GetBaseException().Message}");
    }

    // Caller holds the gate. Claims a bounded batch so a backlog cannot stretch one checkpoint.
    private List<GearRequest> TakeGearRequests(long now)
    {
        var result = new List<GearRequest>();
        if (gearFactory is null) return result;
        foreach (var (id, session) in gearRequests.Take(GearBatch).ToArray())
        {
            gearRequests.Remove(id);
            if (!Ready || !population.Careers.TryGetValue(id, out var career)
                || now < career.NextGearAttemptUtc || GearBlocked(career, now, false)) continue;
            career.NextGearAttemptUtc = now + 60;
            result.Add(new(career, session, career.LastEquipmentOrder, career.LastGearUpdateUtc));
        }
        return result;
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
        var inventory = gearFactory.Generate(inventoryGenerator, inventoryContainers, session, isolated, career, Level(career.Experience), TierBoost(career));
        var equipment = EquipmentTree(JsonNode.Parse(json.Serialize(inventory)!)!.AsObject());
        if (!ValidEquipment(equipment)) throw new InvalidDataException("APBS returned an invalid equipment tree.");
        return equipment;
    }

    // One career per checkpoint never caught up with a large online population. Dead careers go first.
    private const int GearBatch = 10;

    private void ProcessPendingGear(long now)
    {
        if (gearSession is not MongoId session) return;
        foreach (var career in population.Careers.Values.Where(c => !c.Retired && c.IsOnline && c.GearPending
                && (!c.NeedsRegear || WantsGear(c, now))
                && now >= c.NextGearAttemptUtc && now >= c.StoryHoldUntilUtc && now - c.LastEquipmentGeneratedUtc >= 900
                && !IsObservedActive(c, now)).OrderByDescending(c => c.NeedsRegear).ThenBy(c => c.LastGearUpdateUtc)
                .ThenBy(c => c.AccountId).Take(GearBatch))
            gearRequests.TryAdd(career.Id, session);
    }
}
