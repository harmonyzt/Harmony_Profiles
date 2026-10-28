using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace HarmonyProfiles.Server;

public sealed class HideoutProgressionSettings
{
    public double ConstructionSpeed { get; set; } = 1;
    public double MaterialCostMultiplier { get; set; } = 1;
    public double BuiltChanceMultiplier { get; set; } = 1;

    public void Validate()
    {
        if (!double.IsFinite(ConstructionSpeed) || ConstructionSpeed is < .25 or > 4
            || !double.IsFinite(MaterialCostMultiplier) || MaterialCostMultiplier is < .25 or > 4
            || !double.IsFinite(BuiltChanceMultiplier) || BuiltChanceMultiplier is < .25 or > 2)
            throw new InvalidDataException("Hideout speed/cost must be 0.25-4 and built chance must be 0.25-2.");
    }
}

public sealed class HideoutAccessRequest : IRequestData
{
    public string AccountId { get; set; } = "";
}

public sealed record HideoutAccessDecision(int ProtocolVersion, bool Managed, bool CanEnter, string Reason);

public sealed partial class PopulationStore
{
    private sealed class HideoutCohort
    {
        public int MaximumLevel { get; set; }
        public Dictionary<int, HideoutPrior> Modules { get; set; } = new();
    }
    private sealed class HideoutPrior
    {
        public double BuiltChance { get; set; }
        public double MeanLevel { get; set; }
    }
    private static readonly Lazy<List<HideoutCohort>> HideoutCohorts = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HarmonyProfiles.Server.Calibration.hideout-priors.json")
            ?? throw new InvalidDataException("Hideout calibration is missing.");
        return JsonSerializer.Deserialize<List<HideoutCohort>>(stream)
            ?? throw new InvalidDataException("Hideout calibration is empty.");
    });

    private int DesiredHideoutLevel(Career c, int type, double hours, int maximum)
    {
        // Edition privileges are applied separately. Events and wall history are never fabricated here.
        if (type == 3) return maximum;
        var cohort = HideoutCohorts.Value.First(b => Level(c.Experience) <= b.MaximumLevel);
        if (!cohort.Modules.TryGetValue(type, out var prior)) return maximum;
        double focus = c.Archetype == "Timmy" ? .8 : c.Archetype == "Survivor" ? 1.1 : 1;
        double readiness = Math.Clamp(hours / 12, 0, 1);
        double chance = Math.Clamp(prior.BuiltChance * focus * readiness * policy.Hideout.BuiltChanceMultiplier, 0, 1);
        if (prior.BuiltChance <= 0 || Stable(c.Id + ":build:" + type) >= chance) return 0;
        double target = Math.Clamp(prior.MeanLevel / prior.BuiltChance * (.8 + Stable(c.Id + ":stage:" + type) * .4), 1, maximum);
        int whole = (int)target;
        return Math.Clamp(whole + (Stable(c.Id + ":round:" + type) < target - whole ? 1 : 0), 1, maximum);
    }

    public HideoutAccessDecision HideoutAccess(HideoutAccessRequest? request, MongoId session)
    {
        lock (gate)
        {
            if (request?.AccountId is not { Length: > 0 and <= 32 } account || !int.TryParse(account, out int aid))
                return new(1, false, false, "Unknown Hideout owner.");
            if (aid is < 1_500_000_000 or >= 2_000_000_000)
                return new(1, false, false, "Native Hideout permissions apply.");
            if (!Ready) return new(1, true, false, "Harmony-Profiles is unavailable.");
            byAccount.TryGetValue(aid, out var career);
            if (career is null && encounters.TryGetValue(aid, out var encounter) && encounter.SessionId == session.ToString())
                population.Careers.TryGetValue(encounter.CareerId, out career);
            if (career is null) return new(1, true, false, "This account is unavailable.");
            bool accepted = SocialReady && population.Friendships.TryGetValue(session.ToString(), out var friends)
                && friends.TryGetValue(career.Id, out var link) && link.Accepted && !link.Removed && !link.Cancelled;
            return new(1, true, accepted, accepted ? "Visit your friend's Hideout." : "Become accepted friends to enter. Overview remains available.");
        }
    }
}
