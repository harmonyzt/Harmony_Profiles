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
        var assembly =
            AppDomain
                .CurrentDomain.GetAssemblies()
                .FirstOrDefault(a =>
                    a.GetName().Name?.Contains("progressivebotsystem", StringComparison.OrdinalIgnoreCase) == true
                )
            ?? throw new InvalidOperationException("PBT requires Acid's Progressive Bot System.");
        gearFactory = new ApbsGearFactory(assembly, owner, json);
        InstallRaidLoadout(owner);
    }

    private static bool HasVisibleLoadout(JsonObject equipment)
    {
        string? root = equipment["Id"]?.ToString();
        return root is not null
            && equipment["Items"]
                ?.AsArray()
                .OfType<JsonObject>()
                .Any(item =>
                    item["parentId"]?.ToString() == root && item["slotId"]?.ToString() is not (null or "Scabbard" or "Dogtag")
                ) == true;
    }

    private void SeedMissingLoadouts()
    {
        foreach (var career in population.Careers.Values.Where(c => !c.Retired))
        {
            if (career.LastEquipmentOrder == 0 && !career.VisibleLoadout && career.StoryHoldUntilUtc == 0)
                career.NeedsRegear = false;
            if (career.GearVersion == 0)
                career.GearPending = true;
        }

        RestyleCheaterKits();
    }

    // The refresh timer from the last kit must not hold back the regear after a death.
    private static void MarkDead(Career c, long now)
    {
        c.NeedsRegear = true;
        c.GearPending = true;
        c.DiedUtc = now;
        c.NextGearAttemptUtc = 0;
    }

    private long RegearDelay(Career c) =>
        (long)(
            (
                policy.RegearMinimumMinutes
                + Stable(c.Id + ":regear:" + c.DiedUtc) * (policy.RegearMaximumMinutes - policy.RegearMinimumMinutes)
            ) * 60
        );

    // Too poor to rebuy anything: the account runs scav raids and kits up from what it brings out.
    private bool Broke(Career c) => c.WealthRubles < KitCost(c) * .15;

    // A broke account also runs scav raids for its kit, up to 30 minutes more.
    private long GearDelay(Career c) => RegearDelay(c) + (Broke(c) ? (long)(Stable(c.Id + ":scav:" + c.DiedUtc) * 1800) : 0);

    // Only the delay, not the kit: the world dies faster than APBS can regear it.
    private bool WaitingForGear(Career c, long now) => c.NeedsRegear && now - c.DiedUtc < GearDelay(c);

    private bool WantsGear(Career c, long now) =>
        !c.Retired && (c.GearVersion == 0 && c.LastEquipmentAccount == 0 || c.NeedsRegear && now - c.DiedUtc >= GearDelay(c));

    // The APBS worker lags the death rate, so an account heading into a raid rebuys its last kit on the spot instead of going in without one.
    private void RebuyBeforeRaid(Career c, long now)
    {
        if (!c.NeedsRegear || c.Retired || now - c.DiedUtc < RegearDelay(c) || Broke(c) || IsObservedActive(c, now))
            return;
        var previous = CareerEquipment(c);
        if (!ValidEquipment(previous) || !HasVisibleLoadout(previous) || !EquippedWeapons(previous).Any())
            return;
        string gun = GearName(previous, "FirstPrimaryWeapon");
        RecordGear(
            c,
            "rebuy",
            gun.Length > 0 ? $"Rebought the usual {gun} loadout" : "Rebought the usual loadout",
            -KitCost(c),
            now
        );
        c.EquipmentIsSimulated = true;
        c.NeedsRegear = false;
        c.LastEquipmentAccount = 0;
        c.GearVersion = 1;
        c.GearPending = false;
        c.LastGearUpdateUtc = now;
        c.NextGearAttemptUtc = now + Random.Shared.Next(600, 1801);
        dirty = true;
    }

    // APBS runs outside the gate so a profile view never stalls bot spawns.
    private void GearForView(int accountId, MongoId session)
    {
        GearRequest request;
        lock (gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!Ready || gearFactory is null)
                return;
            gearSession = session;
            if (
                !byAccount.TryGetValue(accountId, out var career)
                || !WantsGear(career, now)
                || now < career.NextGearAttemptUtc
                || GearBlocked(career, now, false)
            )
                return;
            career.NextGearAttemptUtc = now + 60;
            request = new(career, session, career.LastEquipmentOrder, career.LastGearUpdateUtc);
        }

        GenerateRequestedGear(request);
    }

    private bool GearBlocked(Career career, long now, bool story) =>
        career.Retired
        || IsObservedActive(career, now)
        || (!story && (now < career.StoryHoldUntilUtc || now - career.LastEquipmentGeneratedUtc < 900));

    // Story regears waiting for a kit, and the ones whose kit is in.
    private readonly Dictionary<int, Career> storyRegears = new();
    private readonly HashSet<int> storyRegeared = new();

    // Caller holds the gate.
    private List<(int Account, Career Career, MongoId Session)> TakeStoryRegears(long now)
    {
        var result = new List<(int, Career, MongoId)>();
        if (gearFactory is null || storyRegears.Count == 0 || GearSession() is not MongoId session)
            return result;
        foreach (var (account, career) in storyRegears.Take(GearBatch).ToArray())
        {
            if (now < career.NextGearAttemptUtc || GearBlocked(career, now, true))
                continue;
            career.NextGearAttemptUtc = now + 60;
            storyRegears.Remove(account);
            result.Add((account, career, session));
        }

        return result;
    }

    // Runs APBS outside the gate, like the profile gear worker.
    private void GenerateStoryRegear(int account, Career career, MongoId session)
    {
        JsonObject equipment;
        try
        {
            equipment = GenerateIsolatedEquipment(career, session);
        }
        catch (Exception ex)
        {
            lock (gate)
                GearFailed(career, ex);
            return;
        }

        lock (gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!Ready || !population.PendingStories.ContainsKey(account) || GearBlocked(career, now, true))
                return;
            try
            {
                ApplyGear(career, equipment, now, story: true);
                storyRegeared.Add(account);
            }
            catch (Exception ex)
            {
                GearFailed(career, ex);
            }
        }
    }

    // A story regear is the kit you saw them change into, so it replaces the whole loadout.
    private void ApplyGear(Career career, JsonObject equipment, long now, bool story = false)
    {
        if (!ValidEquipment(equipment) || !HasVisibleLoadout(equipment))
            throw new InvalidDataException("APBS did not produce a usable equipment tree.");
        if (policy.GearProgression && !story)
        {
            if (career.NeedsRegear)
                equipment = Rebuy(career, equipment, now);
            else if (Upgrading(career))
            {
                if (UpgradeGear(career, equipment, now) is not { } upgraded)
                {
                    KeepGear(career, now);
                    return;
                }

                equipment = upgraded;
            }
        }

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
        if (gearFailures.Add(career.Id))
            logger.Warning(
                $"[PBT] APBS profile gear deferred for account {career.AccountId}; existing equipment retained. {ex.GetBaseException().Message}"
            );
    }

    // Caller holds the gate. Claims a bounded batch so a backlog cannot stretch one checkpoint.
    private List<GearRequest> TakeGearRequests(long now)
    {
        var result = new List<GearRequest>();
        if (gearFactory is null)
            return result;
        foreach (var (id, session) in gearRequests.Take(GearBatch).ToArray())
        {
            gearRequests.Remove(id);
            if (
                !Ready
                || !population.Careers.TryGetValue(id, out var career)
                || now < career.NextGearAttemptUtc
                || GearBlocked(career, now, false)
            )
            {
                gearStats[1]++;
                continue;
            }

            career.NextGearAttemptUtc = now + 60;
            result.Add(new(career, session, career.LastEquipmentOrder, career.LastGearUpdateUtc));
        }

        return result;
    }

    // Runs without the gate. APBS serializes itself through ApbsGearFactory's own lock.
    private void GenerateRequestedGear(GearRequest request)
    {
        JsonObject equipment;
        try
        {
            equipment = GenerateIsolatedEquipment(request.Career, request.Session);
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                gearStats[2]++;
                GearFailed(request.Career, ex);
            }

            return;
        }

        lock (gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // An encounter may have replaced the loadout while APBS was running. Keep the newer one.
            if (
                !Ready
                || request.Career.LastEquipmentOrder != request.Order
                || request.Career.LastGearUpdateUtc != request.UpdatedUtc
                || GearBlocked(request.Career, now, false)
            )
            {
                gearStats[3]++;
                return;
            }

            try
            {
                ApplyGear(request.Career, equipment, now);
                gearStats[4]++;
            }
            catch (Exception ex)
            {
                gearStats[5]++;
                GearFailed(request.Career, ex);
            }
        }
    }

    private JsonObject GenerateIsolatedEquipment(Career career, MongoId session, int extraTiers = 0, bool cheaterLook = true)
    {
        if (gearFactory is null || !bots.Types.TryGetValue(career.Side.ToLowerInvariant(), out var source))
        {
            throw new InvalidOperationException("APBS PMC equipment services are unavailable.");
        }

        // SPT's own cloner. A JSON round trip churned megabytes per regear.
        var isolated = cloner.Clone(source) ?? throw new InvalidDataException("Cannot clone PMC inventory template.");
        // Rack guns are favourites, not what the account can afford today. A broke rebuy is the bottom tier, scav-grade gear.
        int shift =
            extraTiers > 0 ? TierBoost(career) + extraTiers
            : career.NeedsRegear && Broke(career) ? -10
            : TierShift(career, roll: true);
        var inventory = gearFactory.Generate(
            inventoryGenerator,
            inventoryContainers,
            session,
            isolated,
            career,
            Level(career.Experience),
            shift,
            Random.Shared.NextDouble() < policy.NightGearChance
        );
        var equipment = EquipmentTree(ToNode(inventory).AsObject());

        if (!ValidEquipment(equipment))
        {
            throw new InvalidDataException("APBS returned an invalid equipment tree.");
        }

        if (cheaterLook && extraTiers == 0 && WearsCheaterKit(career))
        {
            equipment = CheaterKit(career, equipment);
        }
        else if (cheaterLook && extraTiers == 0)
        {
            career.CheaterKitRolled = false;
        }

        return equipment;
    }

    // Nothing sets gearSession after a restart until the first spawn or view.
    private MongoId? GearSession() => gearSession ??= saves.GetProfiles().Keys.Select(k => (MongoId?)k).FirstOrDefault();

    private const int GearBatch = 10;
    private const int UpgradeSlots = 3;

    // queued, skipped at take, APBS failed, stale after APBS, applied, apply failed
    private readonly long[] gearStats = new long[6];
    private long gearStatsUtc;

    private void ProcessPendingGear(long now)
    {
        if (GearSession() is not MongoId session)
        {
            if (now - gearStatsUtc >= 1800)
            {
                gearStatsUtc = now;
                logger.Warning("[PBT] Gear worker idle: no profile session to generate with.");
            }

            return;
        }

        // The dead regear offline too, or they log back in naked.
        var due = population
            .Careers.Values.Where(c =>
                !c.Retired
                && (c.NeedsRegear ? WantsGear(c, now) : c.IsOnline && c.GearPending)
                && now >= c.NextGearAttemptUtc
                && now >= c.StoryHoldUntilUtc
                && now - c.LastEquipmentGeneratedUtc >= 900
                && !IsObservedActive(c, now)
            )
            .ToList();

        // Most raids end with nothing worth buying. That costs no APBS roll, so it never waits for a batch slot.
        foreach (var career in due.Where(Upgrading).ToArray())
        {
            if (Random.Shared.NextDouble() >= policy.GearUpgradeChance)
            {
                KeepGear(career, now);
                due.Remove(career);
            }
        }

        // A backlog of the dead must not starve the shoppers, so a few slots are theirs.
        var shoppers = due.Where(Upgrading)
            .OrderBy(c => c.LastGearUpdateUtc)
            .ThenBy(c => c.AccountId)
            .Take(UpgradeSlots)
            .ToList();
        foreach (
            var career in shoppers.Concat(
                due.Where(c => !Upgrading(c))
                    .OrderByDescending(c => c.NeedsRegear)
                    .ThenByDescending(c => c.NeedsRegear && Broke(c))
                    .ThenByDescending(c => c.IsOnline)
                    .ThenBy(c => c.LastGearUpdateUtc)
                    .ThenBy(c => c.AccountId)
                    .Take(GearBatch - shoppers.Count)
            )
        )
            if (gearRequests.TryAdd(career.Id, session))
            {
                gearStats[0]++;
            }

        if (now - gearStatsUtc >= 1800)
        {
            gearStatsUtc = now;
            logger.Info(
                $"[PBT] Gear worker: {due.Count} due ({due.Count(c => c.NeedsRegear)} dead), {population.Careers.Values.Count(c => !c.Retired && c.NeedsRegear)} dead in total, {population.Careers.Values.Count(c => !c.Retired && WaitingForGear(c, now))} held by the regear delay, "
                    + $"last 30 min queued {gearStats[0]}, skipped {gearStats[1]}, APBS failed {gearStats[2]}, stale {gearStats[3]}, applied {gearStats[4]}, apply failed {gearStats[5]}."
            );

            Array.Clear(gearStats);
        }
    }
}
