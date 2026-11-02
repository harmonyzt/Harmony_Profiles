using System;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace PBT.Client;

// Live-style matching before a raid: Matching, Looking for server, Preparing the game, Awaiting response.
// Runs after map loading and caching, before the local game is created.
internal static class Matching
{
    private static MethodInfo? create;
    private static readonly System.Collections.Generic.List<MethodInfo> fikaCreates = new();
    private static Func<bool>? fikaHost;
    private static AccessTools.FieldRef<TarkovApplication, RaidSettings>? raidSettings;
    private static bool passing;
    // Set when this game ran the matching, so raid start can say how many careers actually loaded in.
    private static bool matched;
    // Set while a raid loads after a simulated queue.
    private static bool onlineWording;

    internal static void Install(Harmony harmony)
    {
        create = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate));
        raidSettings = AccessTools.FieldRefAccess<TarkovApplication, RaidSettings>("_raidSettings");
        if (create?.ReturnType != typeof(Task) || AccessTools.Property(typeof(TarkovApplication), "Matchmaker") is null)
            throw new MissingMethodException("Local game creation hook unavailable.");
        harmony.Patch(create, prefix: new HarmonyMethod(typeof(Matching), nameof(Create)));
        InstallStatus(harmony);
    }

    private static void InstallStatus(Harmony harmony)
    {
        var update = AccessTools.Method(typeof(EFT.UI.Matchmaker.MatchmakerPlayersController), nameof(EFT.UI.Matchmaker.MatchmakerPlayersController.UpdateMatchingStatus));
        if (update is null) { Plugin.Report("Matching status rename unavailable."); return; }
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(Matching), nameof(Rename)));
    }

    // After a simulated online queue, the vanilla offline wording gives the game away.
    private static void Rename(ref string status)
    {
        if (onlineWording && status == "Local game starting...") status = "Connecting to server";
    }

    // Fika replaces LocalGameCreate, so wait inside its game creation instead. Only the host waits:
    // clients join once the host is ready, so the whole group sits through the same queue.
    internal static void InstallFika(Harmony harmony)
    {
        var backend = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
        var isServer = AccessTools.PropertyGetter(backend, "IsServer");
        var isTransit = AccessTools.PropertyGetter(backend, "IsTransit");
        if (isServer is null || isTransit is null) throw new MissingMethodException("Fika host state unavailable.");
        fikaHost = () => (bool)isServer.Invoke(null, null) && !(bool)isTransit.Invoke(null, null);
        // Players and the headless each have their own copy.
        foreach (var name in new[] { "Fika.Core.Main.Patches.LocalGame.TarkovApplication_LocalGameCreator_Patch", "Fika.Headless.Patches.GameMode.Headless_LocalGameCreator_Patch" })
        {
            var method = AccessTools.TypeByName(name) is { } type ? AccessTools.Method(type, "CreateFikaGame") : null;
            var parameters = method?.GetParameters();
            if (method?.ReturnType != typeof(Task) || parameters is null || parameters.Length < 3
                || parameters[0].ParameterType != typeof(TarkovApplication) || parameters[2].ParameterType != typeof(RaidSettings)) continue;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Matching), nameof(FikaCreate)));
            fikaCreates.Add(method);
        }
        if (fikaCreates.Count == 0) throw new MissingMethodException("Fika game creation hook unavailable.");
        InstallStatus(harmony);
    }

    private static bool Create(TarkovApplication __instance, object[] __args, ref Task __result)
    {
        if (passing) { passing = false; return true; }
        RaidSettings settings;
        try { settings = raidSettings!(__instance); }
        catch { return true; }
        if (settings is null || settings.isInTransition) return true;
        __result = Queue(__instance, settings.LocationId ?? "", () => (Task)create!.Invoke(__instance, __args));
        return false;
    }

    private static bool FikaCreate(MethodBase __originalMethod, object[] __args, ref Task __result)
    {
        if (passing) { passing = false; return true; }
        try
        {
            if (__args[0] is not TarkovApplication app || __args[2] is not RaidSettings settings || settings.isInTransition) return true;
            Func<Task> start = () => (Task)__originalMethod.Invoke(null, __args);
            __result = fikaHost!() ? Queue(app, settings.LocationId ?? "", start) : FollowHost(app, settings.LocationId ?? "", start);
            return false;
        }
        catch (Exception ex) { Plugin.Report("Fika matching skipped: " + ex.Message); return true; }
    }

    private static async Task Queue(TarkovApplication app, string map, Func<Task> start)
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/world/matching",
                new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)));
            bool known = response.Value<int>("ProtocolVersion") == 1;
            int seconds = known ? response.Value<int>("Seconds") : 0;
            matched = known;
            onlineWording = seconds > 0;
            if (seconds > 0)
            {
                var random = new Random();
                int looking = response.Value<int?>("Looking") is int served and > 0 ? served * 1000 : random.Next(5000, 15001);
                Plugin.Trace($"Matching on {map}: {seconds}s, then {looking / 1000}s looking for a server.");
                Status(app, "Matching...");
                await Task.Delay(seconds * 1000);
                Status(app, "Looking for server...");
                await Task.Delay(looking);
                Status(app, "Preparing the game...");
                await Task.Delay(random.Next(200, 1001));
                Status(app, "Awaiting server response...");
                await Task.Delay(100);
            }
            else Plugin.Trace($"Matching on {map}: off or instant.");
            if (known) Plugin.Trace($"Matched with {response.Value<int>("Matched")} PBT profile(s).");
        }
        catch (Exception ex) { Plugin.Report("Matching simulation skipped: " + ex.Message); }
        passing = true;
        await start();
    }

    // Fika clients wait for the host anyway. Show the host's matching stages meanwhile instead of a blank wait.
    private static async Task FollowHost(TarkovApplication app, string map, Func<Task> start)
    {
        passing = true;
        var game = start();
        try
        {
            // The host may still be loading, so keep asking until its matching starts.
            for (int tries = 0; !game.IsCompleted && tries < 60; tries++)
            {
                var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/world/matching/host",
                    new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)));
                int seconds = response.Value<int>("ProtocolVersion") == 1 ? response.Value<int>("Seconds") : 0;
                if (seconds <= 0) { await Task.WhenAny(game, Task.Delay(2000)); continue; }
                long matching = seconds * 1000L, looking = matching + response.Value<int>("Looking") * 1000L, offset = response.Value<long>("ElapsedMs");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (long elapsed = offset; !game.IsCompleted && elapsed < looking; elapsed = offset + clock.ElapsedMilliseconds)
                {
                    Status(app, elapsed < matching ? "Matching..." : "Looking for server...");
                    await Task.WhenAny(game, Task.Delay(500));
                }
                if (!game.IsCompleted) Status(app, "Awaiting server response...");
                break;
            }
        }
        catch (Exception ex) { Plugin.Report("Host matching status unavailable: " + ex.Message); }
        await game;
    }

    // Bots are generated while the raid loads, so the real count is only known once it starts.
    internal static void RaidStarted()
    {
        onlineWording = false;
        if (!matched) return;
        matched = false;
        _ = LogRaid();
    }

    private static async Task LogRaid()
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.GetJsonAsync("/pbt/world/raid"));
            if (response.Value<int>("ProtocolVersion") != 1) return;
            Plugin.Trace($"Raid started with {response.Value<int>("Loaded")} PBT profile(s).");
        }
        catch (Exception ex) { Plugin.Report("Raid profile count unavailable: " + ex.Message); }
    }

    // A headless host has no matchmaker screen.
    private static void Status(TarkovApplication app, string text)
    {
        try { app.Matchmaker?.UpdateMatchingStatus(text); }
        catch { }
    }
}
