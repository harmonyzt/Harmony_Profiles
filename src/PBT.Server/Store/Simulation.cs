using System.Text.Json;
using System.Text.Json.Nodes;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private const double MaximumWealth = 150_000_000;

        private HashSet<string>? observedActive;
        private int observedActiveCount;
        private object? observedActiveSource;
        private long observedActiveUtc = -1;
        private List<PersonalityTemplate> personalities = [];

        private void LoadPersonalities()
        {
            string path = Path.Combine(Path.GetDirectoryName(directory)!, "personalities.json");
            if (!File.Exists(path))
            {
                personalities = DefaultPersonalities();
                AtomicFile.Write(path, JsonSerializer.Serialize(personalities, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            else personalities = JsonSerializer.Deserialize<List<PersonalityTemplate>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Personalities file is empty.");

            ValidatePersonalities(personalities);
        }

        private static List<PersonalityTemplate> DefaultPersonalities()
        {
            return
            [
                new PersonalityTemplate
                {
                    Id = "Timmy",
                    Weight = 30,
                    MinimumLevel = 1,
                    MaximumLevel = 15,
                    MinimumSurvival = .12,
                    MaximumSurvival = .3,
                    MinimumRaidMinutes = 4,
                    MaximumRaidMinutes = 28,
                    ProgressTo = "Casual"
                },
                new PersonalityTemplate(),
                new PersonalityTemplate
                {
                    Id = "Aggressive",
                    Weight = 20,
                    MinimumRaidMinutes = 5,
                    MaximumRaidMinutes = 32
                },
                new PersonalityTemplate
                {
                    Id = "Survivor",
                    Weight = 10,
                    MinimumSurvival = .5,
                    MaximumSurvival = .7,
                    MinimumRaidMinutes = 16,
                    MaximumRaidMinutes = 48
                }
            ];
        }

        private static void ValidatePersonalities(List<PersonalityTemplate> candidates)
        {
            if (candidates.Count is < 1 or > 100 || candidates.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)
                                                                                  || p.Id.Length > 64 || p.Weight is < 1 or > 1000 || p.MinimumLevel < 1
                                                                                  || p.MaximumLevel < p.MinimumLevel || p.MaximumLevel > 79 || !double.IsFinite(p.MinimumDailyHours)
                                                                                  || !double.IsFinite(p.MaximumDailyHours) || p.MinimumDailyHours < 0
                                                                                  || p.MaximumDailyHours < p.MinimumDailyHours || p.MaximumDailyHours > 24
                                                                                  || !double.IsFinite(p.MinimumSurvival) || !double.IsFinite(p.MaximumSurvival)
                                                                                  || p.MinimumSurvival < 0 || p.MaximumSurvival < p.MinimumSurvival || p.MaximumSurvival > 1
                                                                                  || !double.IsFinite(p.MinimumRaidMinutes) || !double.IsFinite(p.MaximumRaidMinutes)
                                                                                  || p.MinimumRaidMinutes < 2 || p.MaximumRaidMinutes < p.MinimumRaidMinutes
                                                                                  || p.MaximumRaidMinutes > 60 || !double.IsFinite(p.TransitionHours) || p.TransitionHours < 1)
                                                 || candidates.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count
                                                 || candidates.Any(p => p.ProgressTo is not null && !candidates.Any(t => t.Id == p.ProgressTo)))
                throw new InvalidDataException("Invalid personality templates; existing population preserved.");
        }

        private PersonalityTemplate Personality(Career c)
        {
            return personalities.FirstOrDefault(p => p.Id == c.Archetype) ?? personalities[0];
        }

        private PersonalityTemplate PickPersonality()
        {
            int roll = Random.Shared.Next(personalities.Sum(p => p.Weight));
            foreach (PersonalityTemplate p in personalities)
            {
                roll -= p.Weight;
                if (roll < 0) return p;
            }

            return personalities[^1];
        }

        private double RaidDuration(Career c, bool random)
        {
            PersonalityTemplate p = Personality(c);
            double lower = Math.Clamp(p.MinimumRaidMinutes, 3, 60);
            double upper = Math.Max(lower, Math.Clamp(p.MaximumRaidMinutes, 3, 60));
            ProgressionPrior prior = Cohort(c);
            double pace = c.Archetype == "Aggressive" ? .85 : c.Archetype == "Survivor" ? 1.15 : 1;
            // Survived raids run long and deaths end early, so the average follows the survival rate.
            double mean = (Survival(c) * prior.SurvivedMinutes + (1 - Survival(c)) * prior.DeathMinutes) * pace * (.85 + Stable(c.Id + ":raid-duration") * .3);
            double minutes = mean * (random ? .5 + Random.Shared.NextDouble() : 1);
            return Math.Clamp(minutes, lower, upper) * 60;
        }

        private void EnsureLife(Career c, long now)
        {
            bool migrating = c.LifeVersion < 2;
            if (migrating)
            {
                // Repair implausibly short history by adding time; never remove published raids or kills.
                c.RaidSeconds = Math.Max(c.RaidSeconds, c.Raids * RaidDuration(c, false));
                c.OnlineSeconds = Math.Max(c.OnlineSeconds, c.RaidSeconds);
                c.TargetRaidSeconds = RaidDuration(c, true);
                c.PendingRaidSeconds = Math.Clamp(c.PendingRaidSeconds, 0, Math.Max(0, c.TargetRaidSeconds - .001));
                c.LifeVersion = 2;
            }

            if (c.NextActivityUtc <= 0)
            {
                c.LastActivityUtc = now;
                c.NextActivityUtc = now + Random.Shared.Next(600, 1801);
            }

            if (migrating || c.RegisteredUtc <= 0)
                c.RegisteredUtc = Math.Max(1, Math.Min(c.RegisteredUtc > 0 ? c.RegisteredUtc : now, now - AccountAge(c, now)));
        }

        private void ApplySimulation(Career c, double seconds)
        {
            int previousRaids = c.Raids, previousSurvived = c.Survived;
            AddRaids(c, seconds, true);
            int completed = c.Raids - previousRaids, survived = c.Survived - previousSurvived;
            int level = Level(c.Experience);
            c.WealthRubles = Math.Clamp(c.WealthRubles + survived * RaidIncome(c, level, true) - (completed - survived) * RaidIncome(c, level, false), 0, MaximumWealth);
            PersonalityTemplate personality = Personality(c);
            if (personality.ProgressTo is not null && (c.RaidSeconds + c.Observed.ActiveSeconds) / 3600 >= personality.TransitionHours)
            {
                c.Archetype = personality.ProgressTo;
                personality = Personality(c);
                c.SurvivalChance = SurvivalFor(c);
            }

            if (completed > 0)
            {
                Develop(c);
                TryPrestige(c, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                c.GearPending = true;
            }
        }

        // Loot value grows with level. A death costs the kit, about a third of a good raid.
        private static double RaidIncome(Career c, int level, bool survived)
        {
            double earned = (40000 + 6000 * level) * (c.Archetype == "Timmy" ? .5 : 1);
            return survived ? earned : earned * .3;
        }

        // One set per second instead of a receipt scan per career.
        private bool IsObservedActive(Career c, long now)
        {
            Dictionary<int, RaidObservation> receipts = population.RaidObservations;
            if (observedActive is null || observedActiveUtc != now || !ReferenceEquals(observedActiveSource, receipts) || observedActiveCount != receipts.Count)
            {
                observedActive = ObservedActiveCareers(now);
                observedActiveUtc = now;
                observedActiveSource = receipts;
                observedActiveCount = receipts.Count;
            }

            return observedActive.Contains(c.Id);
        }

        private void AdvanceOnline(long now)
        {
            foreach (Career c in population.Careers.Values.Where(c => !c.Retired && now >= c.NextActivityUtc))
            {
                if (IsObservedActive(c, now) || now < c.StoryHoldUntilUtc) continue;
                // Raid time itself is earned raid by raid in the world tick. This only decides who plays.
                Activity a = ActivityOf(c);
                int interval = Random.Shared.Next(600, 1801);
                if (a.State == "Offline")
                {
                    if (Random.Shared.NextDouble() < SessionStartChance(c, interval)) GoOnline(c, a, now);
                }
                else if (SessionOver(c, now))
                {
                    if (a.State == "Menu" && !a.Held) GoOffline(c, a, now);
                    else a.WantsOffline = true;
                }

                if (c.IsOnline) c.Achievements = EarnedMilestones(c, now);
                c.LastActivityUtc = now;
                c.NextActivityUtc = c.IsOnline ? Math.Min(now + interval, Math.Max(now + 60, c.SessionEndUtc)) : now + interval;
                worldDirty = true;
            }
        }

        private bool TryRegear(Career c, bool story = false)
        {
            c.GearPending = true;
            if (!story && !c.IsOnline || GearSession() is not SPTarkov.Server.Core.Models.Common.MongoId session) return false;
            return GenerateCareerGear(c, session, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), story);
        }

        private static string? PrimaryWeapon(JsonObject equipment)
        {
            return equipment["Items"]?.AsArray().OfType<JsonObject>().FirstOrDefault(i => i["slotId"]?.ToString() == "FirstPrimaryWeapon")?["_tpl"]?.ToString();
        }

        private static JsonObject NakedEquipment(JsonObject source)
        {
            string root = source["Id"]!.GetValue<string>();
            JsonObject[] all = source["Items"]!.AsArray().OfType<JsonObject>().ToArray();
            HashSet<string> keep = new HashSet<string>(all.Where(i => i["parentId"]?.ToString() == root && i["slotId"]?.ToString() == "Scabbard")
                .Select(i => i["_id"]!.GetValue<string>()));
            bool changed;
            do
            {
                changed = false;
                foreach (JsonObject i in all)
                {
                    if (i["parentId"] is { } p && keep.Contains(p.ToString())) changed |= keep.Add(i["_id"]!.GetValue<string>());
                }
            } while (changed);

            keep.Add(root);
            return new JsonObject
            {
                ["Id"] = root,
                ["Items"] = new JsonArray(all.Where(i => keep.Contains(i["_id"]!.GetValue<string>())).Select(i => i.DeepClone()).ToArray())
            };
        }

        private JsonObject RepairEquipment(JsonObject source)
        {
            try
            {
                string root = source["Id"]!.GetValue<string>();
                JsonObject[] valid = source["Items"]!.AsArray().OfType<JsonObject>()
                    .Where(i => IsId(i["_tpl"]?.ToString()) && templates.Items.ContainsKey(new(i["_tpl"]!.ToString()))).ToArray();
                HashSet<string> keep = new HashSet<string>
                {
                    root
                };
                bool changed;
                do
                {
                    changed = false;
                    foreach (JsonObject i in valid)
                    {
                        if (i["parentId"] is { } p && keep.Contains(p.ToString())) changed |= keep.Add(i["_id"]!.ToString());
                    }
                } while (changed);

                JsonObject result = new JsonObject
                {
                    ["Id"] = root,
                    ["Items"] = new JsonArray(valid.Where(i => keep.Contains(i["_id"]!.ToString())).Select(i => i.DeepClone()).ToArray())
                };
                return ValidEquipment(result) ? result : EmptyEquipment();
            }
            catch
            {
                return EmptyEquipment();
            }
        }
    }
}
