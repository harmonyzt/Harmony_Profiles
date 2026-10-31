using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace PBT.Client;

internal static class RaidActivity
{
    private sealed class Observation
    {
        public int AccountId;
        public string RuntimeProfileId = "";
        public Player? Player;
        public float Started;
        public float? Stopped;
        public bool Died;
        public int PmcKills;
        public int ScavKills;
        public int UsecKills;
        public int BearKills;
    }

    private static readonly Dictionary<string, Observation> bots = new();
    private static readonly HashSet<string> deaths = new();
    private static string raid = "";
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
        raid = raidId;
        currentWorld = world;
        observing = Supported;
        nextUpload = Time.realtimeSinceStartup + 60;
        if (observing)
            foreach (var player in world.RegisteredPlayers) Registered(player);
    }

    private static void Registered(IPlayer __0)
    {
        try { Register(__0); }
        catch (Exception ex) { Plugin.Report("Raid activation observation failed: " + ex.Message); }
    }

    private static void Register(IPlayer __0)
    {
        if (!observing || __0 is not Player player || !player.IsAI || bots.Count >= 512
            || player.Profile.Side is not (EPlayerSide.Bear or EPlayerSide.Usec)
            || bots.ContainsKey(player.ProfileId) || player.HealthController?.IsAlive != true
            || !int.TryParse(player.Profile.AccountId, out int account) || account < 1500000000 || account >= 2000000000) return;
        bots.Add(player.ProfileId, new Observation
        {
            AccountId = account, RuntimeProfileId = player.ProfileId, Player = player, Started = Time.realtimeSinceStartup
        });
    }

    private static void Unregistered(IPlayer __0)
    {
        if (observing && bots.TryGetValue(__0.ProfileId, out var bot))
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
        catch { }
    }

    internal static void Killed(Player victim, IPlayer aggressor, bool alreadyFound)
    {
        if (!observing || alreadyFound || ReferenceEquals(aggressor, victim) || !deaths.Add(victim.ProfileId)) return;
        if (bots.TryGetValue(victim.ProfileId, out var dead))
        {
            dead.Died = true;
            dead.Stopped ??= Time.realtimeSinceStartup;
        }
        if (bots.TryGetValue(aggressor.ProfileId, out var killer))
        {
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

    internal static void Tick()
    {
        if (!observing || Time.realtimeSinceStartup < nextUpload || !pending.IsCompleted) return;
        nextUpload = Time.realtimeSinceStartup + 60;
        if (currentWorld != null)
            foreach (var player in currentWorld.RegisteredPlayers) Registered(player);
        Flush();
    }

    internal static void End()
    {
        try { EndCore(); }
        catch (Exception ex) { Plugin.Report("Raid observation finalization failed: " + ex.Message); }
    }

    private static void EndCore()
    {
        if (!observing) return;
        observing = false;
        currentWorld = null;
        foreach (var bot in bots.Values) bot.Stopped ??= Time.realtimeSinceStartup;
        Flush();
    }

    private static void Flush()
    {
        if (bots.Count == 0) return;
        foreach (var bot in bots.Values) CheckDead(bot);
        var entries = new JArray();
        foreach (var bot in bots.Values)
            entries.Add(new JObject
            {
                ["AccountId"] = bot.AccountId, ["RuntimeProfileId"] = bot.RuntimeProfileId,
                ["ActiveSeconds"] = Math.Max(0, Math.Min(21600, (int)((bot.Stopped ?? Time.realtimeSinceStartup) - bot.Started))),
                ["Finished"] = bot.Stopped.HasValue, ["Died"] = bot.Died, ["PmcKills"] = bot.PmcKills, ["ScavKills"] = bot.ScavKills, ["UsecKills"] = bot.UsecKills, ["BearKills"] = bot.BearKills
            });
        string payload = new JObject { ["ProtocolVersion"] = 1, ["RaidId"] = raid, ["Bots"] = entries }.ToString(Formatting.None);
        pending = SendAfter(pending, payload);
    }

    private static async Task SendAfter(Task previous, string payload)
    {
        await previous;
        try
        {
            var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/activity", payload));
            if (response.Value<int>("ProtocolVersion") != 1) Plugin.Report("Raid observation protocol mismatch.");
            else Plugin.Trace("Raid observations saved: " + response.Value<int>("Accepted") + ".");
        }
        catch (Exception ex) { Plugin.Report("Raid observation upload failed; later cumulative reports can retry. " + ex.Message); }
    }
}
