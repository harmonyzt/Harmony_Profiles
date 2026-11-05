namespace PBT.Client
{
    internal static class RaidActivity
    {
        private static readonly Dictionary<string, Observation> bots = new Dictionary<string, Observation>();
        private static readonly HashSet<string> deaths = new HashSet<string>();
        private static string raid = "";
        private static string map = "";
        private static int loggedCount;
        private static bool observing;
        private static float nextUpload;
        private static GameWorld? currentWorld;
        private static Task pending = Task.CompletedTask;
        internal static bool Supported;
        internal static bool Installed { get; private set; }

        internal static void Install(Harmony harmony)
        {
            var register = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterPlayer), new[] { typeof(IPlayer) });
            var unregister = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.UnregisterPlayer), new[] { typeof(IPlayer) });
            var dispose = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose), Type.EmptyTypes);
            if (register is null || unregister is null || dispose is null)
                throw new MissingMethodException("Raid observation hooks unavailable.");
            harmony.Patch(register, postfix: new HarmonyMethod(typeof(RaidActivity), nameof(Registered)));
            harmony.Patch(unregister, prefix: new HarmonyMethod(typeof(RaidActivity), nameof(Unregistered)));
            harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(RaidActivity), nameof(End)));
            Installed = true;
        }

        internal static void Start(GameWorld world, string raidId)
        {
            End();
            bots.Clear();
            deaths.Clear();
            loggedCount = 0;
            raid = raidId;
            map = world.LocationId ?? "";
            currentWorld = world;
            observing = Supported;
            nextUpload = Time.realtimeSinceStartup + 60;
            if (observing)
                foreach (var player in world.RegisteredPlayers)
                {
                    Registered(player);
                }
        }

        private static void Registered(IPlayer __0)
        {
            try
            {
                Register(__0);
            }
            catch (Exception ex)
            {
                Plugin.Report("Raid activation observation failed: " + ex.Message);
            }
        }

        private static void Register(IPlayer __0)
        {
            if (!observing || __0 is not Player player || !player.IsAI || bots.Count >= 512 || player.Profile.Side is not (EPlayerSide.Bear or EPlayerSide.Usec)
                || bots.ContainsKey(player.ProfileId) || player.HealthController?.IsAlive != true || !int.TryParse(player.Profile.AccountId, out int account)
                || account < 1500000000 || account >= 2000000000) return;
            bots.Add(player.ProfileId, new Observation
            {
                AccountId = account,
                RuntimeProfileId = player.ProfileId,
                Player = player,
                Started = Time.realtimeSinceStartup
            });
        }

        private static void Unregistered(IPlayer __0)
        {
            if (observing && bots.TryGetValue(__0.ProfileId, out Observation? bot))
            {
                CheckDead(bot);
                bot.Stopped ??= Time.realtimeSinceStartup;
            }
        }

        // The kill hook only fires when the game credits an aggressor, and Fika skips that for most observed bots.
        private static void CheckDead(Observation bot)
        {
            try
            {
                if (bot.Died || bot.Player == null || bot.Player.HealthController is not { IsAlive: false }) return;
                bot.Died = true;
                bot.Stopped ??= Time.realtimeSinceStartup;
            }
            catch
            {
            }
        }

        internal static void Killed(Player victim, IPlayer aggressor, bool alreadyFound)
        {
            if (!observing || alreadyFound || ReferenceEquals(aggressor, victim) || !deaths.Add(victim.ProfileId)) return;
            if (bots.TryGetValue(victim.ProfileId, out Observation? dead))
            {
                dead.Died = true;
                dead.Stopped ??= Time.realtimeSinceStartup;
                if (aggressor is Player { IsYourPlayer: true })
                {
                    dead.KilledByPlayer = true;
                    try
                    {
                        RecordContext(dead, victim);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Trace("Kill context unavailable: " + ex.Message);
                    }
                }
            }

            if (aggressor != null && bots.TryGetValue(aggressor.ProfileId, out Observation? killer))
            {
                if (victim.IsYourPlayer) killer.KilledPlayer = true;
                if (victim.Profile.Side == EPlayerSide.Savage) killer.ScavKills++;
                else if (victim.Profile.Side is EPlayerSide.Bear or EPlayerSide.Usec)
                {
                    killer.PmcKills++;
                    if (victim.Profile.Side == EPlayerSide.Usec) killer.UsecKills++;
                    else killer.BearKills++;
                }
            }

            if (victim.IsYourPlayer) End();
        }

        // Bot messages mention a kill at an exit or on a full bag.
        private static void RecordContext(Observation dead, Player victim)
        {
            var exits = currentWorld?.ExfiltrationController?.ExfiltrationPoints;
            if (exits != null)
                foreach (var exit in exits)
                {
                    if (exit != null && Vector3.Distance(exit.transform.position, victim.Position) <= 30)
                    {
                        dead.NearExtract = true;
                        break;
                    }
                }

            long value = 0;
            var items = victim.Profile?.Inventory?.AllRealPlayerItems;
            if (items != null)
                foreach (var item in items)
                {
                    value += (long)(item.Template?.CreditsPrice ?? 0) * Math.Max(1, item.StackObjectsCount);
                }

            dead.LootValue = (int)Math.Min(int.MaxValue, value);
        }

        internal static void Tick()
        {
            if (!observing || Time.realtimeSinceStartup < nextUpload || !pending.IsCompleted) return;
            nextUpload = Time.realtimeSinceStartup + 60;
            if (currentWorld != null)
                foreach (var player in currentWorld.RegisteredPlayers)
                {
                    Registered(player);
                }

            Flush();
        }

        internal static void End()
        {
            try
            {
                EndCore();
            }
            catch (Exception ex)
            {
                Plugin.Report("Raid observation finalization failed: " + ex.Message);
            }
        }

        private static void EndCore()
        {
            if (!observing) return;
            observing = false;
            currentWorld = null;
            foreach (Observation? bot in bots.Values)
            {
                bot.Stopped ??= Time.realtimeSinceStartup;
            }

            Flush();
            if (bots.Count > 0)
            {
                int died = 0;
                foreach (Observation? bot in bots.Values)
                {
                    if (bot.Died) died++;
                }

                Plugin.Trace($"Raid over: met {bots.Count} PBT career{(bots.Count == 1 ? "" : "s")} on {map}, {died} died.");
            }
        }

        private static void Flush()
        {
            if (bots.Count == 0) return;
            if (observing && bots.Count != loggedCount)
            {
                loggedCount = bots.Count;
                Plugin.Trace($"{bots.Count} PBT career{(bots.Count == 1 ? "" : "s")} in this raid so far.");
            }

            foreach (Observation? bot in bots.Values)
            {
                CheckDead(bot);
            }

            var entries = new JArray();
            foreach (Observation? bot in bots.Values)
            {
                entries.Add(new JObject
                {
                    ["AccountId"] = bot.AccountId,
                    ["RuntimeProfileId"] = bot.RuntimeProfileId,
                    ["ActiveSeconds"] = Math.Max(0, Math.Min(21600, (int)((bot.Stopped ?? Time.realtimeSinceStartup) - bot.Started))),
                    ["Finished"] = bot.Stopped.HasValue,
                    ["Died"] = bot.Died,
                    ["PmcKills"] = bot.PmcKills,
                    ["ScavKills"] = bot.ScavKills,
                    ["UsecKills"] = bot.UsecKills,
                    ["BearKills"] = bot.BearKills,
                    ["KilledByPlayer"] = bot.KilledByPlayer,
                    ["KilledPlayer"] = bot.KilledPlayer,
                    ["NearExtract"] = bot.NearExtract,
                    ["LootValue"] = bot.LootValue
                });
            }

            string payload = new JObject
            {
                ["ProtocolVersion"] = 1,
                ["RaidId"] = raid,
                ["Map"] = map,
                ["Bots"] = entries
            }.ToString(Formatting.None);
            pending = SendAfter(pending, payload);
        }

        private static async Task SendAfter(Task previous, string payload)
        {
            await previous;
            try
            {
                var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/activity", payload));
                if (response.Value<int>("ProtocolVersion") != 1) Plugin.Report("Raid observation protocol mismatch.");
            }
            catch (Exception ex)
            {
                Plugin.Report("Raid observation upload failed; later cumulative reports can retry. " + ex.Message);
            }
        }

        private sealed class Observation
        {
            public int AccountId;
            public int BearKills;
            public bool Died;
            public bool KilledByPlayer;
            public bool KilledPlayer;
            public int LootValue;
            public bool NearExtract;
            public Player? Player;
            public int PmcKills;
            public string RuntimeProfileId = "";
            public int ScavKills;
            public float Started;
            public float? Stopped;
            public int UsecKills;
        }
    }
}
