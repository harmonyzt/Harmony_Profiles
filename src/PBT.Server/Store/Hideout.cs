using System.Reflection;
using System.Text.Json;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private static readonly Lazy<List<HideoutCohort>> HideoutCohorts = new Lazy<List<HideoutCohort>>(() =>
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PBT.Server.Calibration.hideout-priors.json")
                                  ?? throw new InvalidDataException("Hideout calibration is missing.");
            return JsonSerializer.Deserialize<List<HideoutCohort>>(stream) ?? throw new InvalidDataException("Hideout calibration is empty.");
        });

        private int DesiredHideoutLevel(Career c, int type, double hours, int maximum)
        {
            if (type == 3) return maximum;
            HideoutCohort cohort = HideoutCohorts.Value.First(b => Level(c.Experience) <= b.MaximumLevel);
            if (!cohort.Modules.TryGetValue(type, out HideoutPrior? prior)) return maximum;
            double focus = c.Archetype == "Timmy" ? .8 : c.Archetype == "Survivor" ? 1.1 : 1;
            double readiness = Math.Clamp(hours / 12, 0, 1);
            bool neglected = Stable(c.Id + ":neglect") < policy.Hideout.NeglectedShare;
            double chance = Math.Clamp(prior.BuiltChance * focus * readiness * policy.Hideout.BuiltChanceMultiplier * (neglected ? .4 : 1), 0, 1);
            if (prior.BuiltChance <= 0 || Stable(c.Id + ":build:" + type) >= chance) return 0;
            if (neglected) return 1;
            double target = Math.Clamp(prior.MeanLevel / prior.BuiltChance * (.8 + Stable(c.Id + ":stage:" + type) * .4), 1, maximum);
            int whole = (int)target;
            return Math.Clamp(whole + (Stable(c.Id + ":round:" + type) < target - whole ? 1 : 0), 1, maximum);
        }

        public HideoutAccessDecision HideoutAccess(HideoutAccessRequest? request, MongoId session)
        {
            lock (gate)
            {
                if (request?.AccountId is not { Length: > 0 and <= 32 } account || !int.TryParse(account, out int aid))
                    return new HideoutAccessDecision(1, false, false, "Unknown Hideout owner.");
                if (aid is < 1_500_000_000 or >= 2_000_000_000)
                    return new HideoutAccessDecision(1, false, false, "Native Hideout permissions apply.");
                if (!Ready) return new HideoutAccessDecision(1, true, false, "PBT is unavailable.");
                byAccount.TryGetValue(aid, out Career? career);
                if (career is null && encounters.TryGetValue(aid, out Encounter? encounter) && CanAccess(encounter, session))
                    population.Careers.TryGetValue(encounter.CareerId, out career);
                if (career is null) return new HideoutAccessDecision(1, true, false, "This account is unavailable.");
                bool accepted = SocialReady && population.Friendships.TryGetValue(session.ToString(), out Dictionary<string, Friendship>? friends)
                                            && friends.TryGetValue(career.Id, out Friendship? link) && link.Accepted && !link.Removed && !link.Cancelled;
                return new HideoutAccessDecision(1, true, accepted, accepted ? "Visit your friend's Hideout." : "Become accepted friends to enter. Overview remains available.");
            }
        }

        private sealed class HideoutCohort
        {
            public int MaximumLevel { get; }
            public Dictionary<int, HideoutPrior> Modules { get; } = new Dictionary<int, HideoutPrior>();
        }

        private sealed class HideoutPrior
        {
            public double BuiltChance { get; }
            public double MeanLevel { get; }
        }
    }
}
