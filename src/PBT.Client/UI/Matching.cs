using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace PBT.Client.UI;

// Matching, Looking for server, Preparing the game, Awaiting response
internal static class Matching
{
    private static MethodInfo? create;
    private static readonly List<MethodInfo> fikaCreates = new();
    private static Func<bool>? fikaHost;
    private static AccessTools.FieldRef<TarkovApplication, RaidSettings>? raidSettings;
    private static bool passing;
    private static bool onlineWording;
    private static int lobbySize;

    // One simulated matching at a time. Once the game reports its own loading status, the fake ones stop.
    private static volatile int run;
    private static volatile bool simulating,
        handedOver,
        staleNoticed;

    [ThreadStatic]
    private static bool ours;

    internal static void Install(Harmony harmony)
    {
        create = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate));
        raidSettings = AccessTools.FieldRefAccess<TarkovApplication, RaidSettings>("_raidSettings");
        if (create?.ReturnType != typeof(Task) || AccessTools.Property(typeof(TarkovApplication), "Matchmaker") is null)
        {
            throw new MissingMethodException("Local game creation hook unavailable.");
        }

        harmony.Patch(create, prefix: new HarmonyMethod(typeof(Matching), nameof(Create)));
        InstallStatus(harmony);

        var bots = AccessTools.Method(typeof(LocalGame), nameof(LocalGame.vmethod_1));
        if (bots is null)
        {
            Plugin.Report("Awaiting players status unavailable.");
        }
        else
        {
            harmony.Patch(bots, postfix: new HarmonyMethod(typeof(Matching), nameof(AwaitingPlayers)));
        }
    }

    private static void InstallStatus(Harmony harmony)
    {
        var update = AccessTools.Method(
            typeof(EFT.UI.Matchmaker.MatchmakerPlayersController),
            nameof(EFT.UI.Matchmaker.MatchmakerPlayersController.UpdateMatchingStatus)
        );
        if (update is null)
        {
            Plugin.Report("Matching status rename unavailable.");
            return;
        }

        harmony.Patch(update, prefix: new HarmonyMethod(typeof(Matching), nameof(Rename)));
    }

    // After a simulated online queue, the vanilla offline wording gives the game away.
    private static void Rename(ref string status)
    {
        if (simulating && !ours && !handedOver)
        {
            handedOver = true;
            Plugin.Trace($"Matching guard: the game reported \"{status}\" during simulated matching, handing the screen over.");
        }

        if (onlineWording && status == "Local game starting...")
            status = "Connecting to server";
    }

    private static int BeginSimulation()
    {
        handedOver = false;
        staleNoticed = false;
        simulating = true;
        return ++run;
    }

    private static void EndSimulation(int id)
    {
        if (id == run)
            simulating = false;
    }

    // Simulated statuses never overwrite the game's real loading progress or a newer matching.
    private static void Simulated(TarkovApplication app, int id, string text)
    {
        if (id != run)
        {
            if (!staleNoticed)
            {
                staleNoticed = true;
                Plugin.Trace($"Matching guard: an older matching tried to show \"{text}\", ignored.");
            }

            return;
        }

        if (handedOver)
            return;
        ours = true;
        try
        {
            Status(app, text);
        }
        finally
        {
            ours = false;
        }
    }

    // Fika replaces LocalGameCreate. Only the host waits, clients join once it is ready.
    internal static void InstallFika(Harmony harmony)
    {
        var backend = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
        var isServer = AccessTools.PropertyGetter(backend, "IsServer");
        var isTransit = AccessTools.PropertyGetter(backend, "IsTransit");

        if (isServer is null || isTransit is null)
            throw new MissingMethodException("Fika host state unavailable.");

        fikaHost = () => (bool)isServer.Invoke(null, null) && !(bool)isTransit.Invoke(null, null);
        foreach (
            var name in new[]
            {
                "Fika.Core.Main.Patches.LocalGame.TarkovApplication_LocalGameCreator_Patch",
                "Fika.Headless.Patches.GameMode.Headless_LocalGameCreator_Patch",
            }
        )
        {
            var method = AccessTools.TypeByName(name) is { } type ? AccessTools.Method(type, "CreateFikaGame") : null;
            var parameters = method?.GetParameters();
            if (
                method?.ReturnType != typeof(Task)
                || parameters is null
                || parameters.Length < 3
                || parameters[0].ParameterType != typeof(TarkovApplication)
                || parameters[2].ParameterType != typeof(RaidSettings)
            )
                continue;

            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Matching), nameof(FikaCreate)));
            fikaCreates.Add(method);
        }

        if (fikaCreates.Count == 0)
            throw new MissingMethodException("Fika game creation hook unavailable.");
        InstallStatus(harmony);
    }

    private static bool Create(TarkovApplication __instance, object[] __args, ref Task __result)
    {
        if (passing)
        {
            passing = false;
            return true;
        }

        RaidSettings settings;
        try
        {
            settings = raidSettings!(__instance);
        }
        catch
        {
            return true;
        }

        if (settings is null || settings.isInTransition)
            return true;
        __result = Queue(__instance, settings.LocationId ?? "", () => (Task)create!.Invoke(__instance, __args), forward: true);

        return false;
    }

    private static bool FikaCreate(MethodBase __originalMethod, object[] __args, ref Task __result)
    {
        if (passing)
        {
            passing = false;
            return true;
        }

        try
        {
            if (__args[0] is not TarkovApplication app || __args[2] is not RaidSettings settings || settings.isInTransition)
                return true;
            Func<Task> start = () => (Task)__originalMethod.Invoke(null, __args);
            __result = fikaHost!()
                ? Queue(app, settings.LocationId ?? "", start)
                : FollowHost(app, settings.LocationId ?? "", start);
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Report("Fika matching skipped: " + ex.Message);
            return true;
        }
    }

    private static async Task Queue(TarkovApplication app, string map, Func<Task> start, bool forward = false)
    {
        int id = BeginSimulation();
        try
        {
            var response = JObject.Parse(
                await RequestHandler.PostJsonAsync(
                    "/pbt/world/matching",
                    new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)
                )
            );
            bool known = response.Value<int>("ProtocolVersion") == 1;
            int seconds = known ? response.Value<int>("Seconds") : 0;
            bool wait = known && response.Value<bool?>("Wait") == true;
            onlineWording = seconds > 0 || wait;
            lobbySize = known ? response.Value<int>("Matched") : 0;
            Connection.Matched(known ? response["Server"] : null);
            if (wait)
            {
                int target = response.Value<int>("Target");
                Plugin.Trace($"Matching on {map}: waiting for {target} players near your level.");
                Simulated(app, id, "Matching...");
                int looking = 0,
                    failed = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // The server gives up on its own after its longest wait. This only guards against a server that stopped answering.
                while (clock.Elapsed.TotalSeconds < 1900)
                {
                    await Task.Delay(2000);
                    JObject poll;
                    try
                    {
                        poll = JObject.Parse(
                            await RequestHandler.PostJsonAsync(
                                "/pbt/world/matching/poll",
                                new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)
                            )
                        );
                        failed = 0;
                    }
                    catch (Exception ex) when (++failed < 3)
                    {
                        // Giving up on one dropped poll leaves the server's wait open and Fika clients on a stale timeline.
                        Plugin.Report($"Matching poll failed ({failed}/3), retrying: {ex.Message}");
                        continue;
                    }

                    if (poll.Value<int>("ProtocolVersion") != 1)
                        break;
                    lobbySize = poll.Value<int>("Found");
                    if (!poll.Value<bool>("Done"))
                        continue;
                    looking = poll.Value<int>("Looking") * 1000;
                    break;
                }

                Plugin.Trace($"Matched on {map} after {(int)clock.Elapsed.TotalSeconds}s with {lobbySize} of {target} players.");
                Simulated(app, id, "Looking for server...");
                await Task.Delay(Math.Max(1000, looking));
                Simulated(app, id, "Preparing the game...");
                await Task.Delay(new Random().Next(200, 1001));
                Simulated(app, id, "Awaiting server response...");
                await Task.Delay(100);
            }
            else if (seconds > 0)
            {
                var random = new Random();
                int looking = response.Value<int?>("Looking") is int served and > 0 ? served * 1000 : random.Next(5000, 15001);
                Plugin.Trace($"Matching on {map}: {seconds}s, then {looking / 1000}s looking for a server.");
                Simulated(app, id, "Matching...");
                await Task.Delay(seconds * 1000);
                Simulated(app, id, "Looking for server...");
                await Task.Delay(looking);
                Simulated(app, id, "Preparing the game...");
                await Task.Delay(random.Next(200, 1001));
                Simulated(app, id, "Awaiting server response...");
                await Task.Delay(100);
            }
            else
                Plugin.Trace($"Matching on {map}: off or instant.");

            if (known)
                Plugin.Trace($"Matched with {response.Value<int>("Matched")} PBT profile(s).");
        }
        catch (Exception ex)
        {
            Plugin.Report("Matching simulation skipped: " + ex.Message);
        }

        EndSimulation(id);
        // The previous raid's game can still be the singleton while the next one is being created.
        var previous = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
        passing = true;
        Task game;
        try
        {
            game = start();
        }
        finally
        {
            passing = false;
        }
        if (forward && onlineWording)
            await ForwardStatus(app, game, previous);
        await game;
    }

    // Offline creation never forwards the game's own statuses, so loading sat on "Connecting to server".
    private static async Task ForwardStatus(TarkovApplication app, Task game, AbstractGame? previous)
    {
        Action<string, float?> relay = (status, progress) => app.Matchmaker?.UpdateMatchingStatus(status, progress);
        LocalGame? local = null;
        try
        {
            while (!game.IsCompleted && local is null)
            {
                if (
                    Singleton<AbstractGame>.Instantiated
                    && Singleton<AbstractGame>.Instance is LocalGame created
                    && created != previous
                )
                {
                    local = created;
                    local.OnMatchingStatusChanged += relay;
                    Status(app, "Awaiting session start...");
                }
                else
                    await Task.WhenAny(game, Task.Delay(100));
            }

            await game;
        }
        catch { }
        finally
        {
            if (local is not null)
                local.OnMatchingStatusChanged -= relay;
        }
    }

    // Live shows this between loot and the countdown, which is where offline spawns its bots.
    private static void AwaitingPlayers(LocalGame __instance, ref Task __result)
    {
        if (onlineWording && __result is not null)
            __result = TrackPlayers(__instance, __result);
    }

    // Counts the server's players in as their PMCs spawn, then hands over to the countdown with a status of its own.
    private static async Task TrackPlayers(LocalGame game, Task spawning)
    {
        int total = Math.Max(1, lobbySize) + 1,
            shown = -1;

        void Show(int percent)
        {
            percent = Math.Min(100, percent);
            if (percent <= shown)
                return;
            shown = percent;
            // The game appends the percent from the progress value itself.
            game.SetMatchmakerStatus("Awaiting players...", percent / 100f);
        }

        Show(100 / total);
        while (!spawning.IsCompleted)
        {
            try
            {
                Show(100 * (1 + LoadedPmcs(game)) / total);
            }
            catch { }

            await Task.WhenAny(spawning, Task.Delay(250));
        }

        await spawning;
        // Live servers finish loading their last players over a second or two.
        var random = new Random();
        while (shown < 100)
        {
            Show(shown + random.Next(8, 26));
            await Task.Delay(random.Next(120, 351));
        }

        game.SetMatchmakerStatus("Starting the game...");
    }

    private static int LoadedPmcs(LocalGame game)
    {
        int count = 0;
        var players = game.GameWorld?.RegisteredPlayers;
        if (players is null)
            return 0;
        foreach (var player in players)
            if (player is Player { IsAI: true } p && p.Profile?.Side is EPlayerSide.Bear or EPlayerSide.Usec)
                count++;
        return count;
    }

    // Fika clients mirror the host's matching stages while they wait.
    private static async Task FollowHost(TarkovApplication app, string map, Func<Task> start)
    {
        Connection.Matched(null);
        passing = true;
        Task game;
        try
        {
            game = start();
        }
        finally
        {
            passing = false;
        }

        int id = BeginSimulation();
        try
        {
            bool followed = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // The host's timeline is re-read every couple of seconds: a wait-for-players match only learns its real length when it fills.
            while (!game.IsCompleted && !handedOver && clock.Elapsed.TotalSeconds < 1900)
            {
                var response = JObject.Parse(
                    await RequestHandler.PostJsonAsync(
                        "/pbt/world/matching/host",
                        new JObject { ["Map"] = map }.ToString(Newtonsoft.Json.Formatting.None)
                    )
                );
                int seconds = response.Value<int>("ProtocolVersion") == 1 ? response.Value<int>("Seconds") : 0;
                if (seconds <= 0)
                {
                    if (followed)
                    {
                        Plugin.Trace("Matching guard: the host's matching timeline ended, waiting for the game.");
                        break;
                    }

                    if (clock.Elapsed.TotalSeconds >= 120)
                        break;
                    await Task.WhenAny(game, Task.Delay(2000));
                    continue;
                }

                followed = true;
                long matching = seconds * 1000L,
                    looking = matching + response.Value<int>("Looking") * 1000L,
                    offset = response.Value<long>("ElapsedMs");
                var since = System.Diagnostics.Stopwatch.StartNew();
                long elapsed = offset;
                for (; !game.IsCompleted && !handedOver && elapsed < looking && since.ElapsedMilliseconds < 2000; elapsed = offset + since.ElapsedMilliseconds)
                {
                    Simulated(app, id, elapsed < matching ? "Matching..." : "Looking for server...");
                    await Task.WhenAny(game, Task.Delay(500));
                }

                if (elapsed >= looking)
                    break;
            }

            if (handedOver)
                Plugin.Trace("Matching guard: stopped mirroring the host, the game is loading.");
            else if (followed && !game.IsCompleted)
                Simulated(app, id, "Awaiting server response...");
        }
        catch (Exception ex)
        {
            Plugin.Report("Host matching status unavailable: " + ex.Message);
        }
        finally
        {
            EndSimulation(id);
        }

        await game;
    }

    // Bots are generated while the raid loads, so the real count is only known once it starts.
    internal static void RaidStarted()
    {
        onlineWording = false;
        _ = LogRaid();
    }

    private static async Task LogRaid()
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.GetJsonAsync("/pbt/world/raid"));
            if (response.Value<int>("ProtocolVersion") != 1)
                return;
            int loaded = response.Value<int>("Loaded");
            RaidActivity.Expect(loaded);
            Plugin.Trace($"Raid started with {loaded} PBT profile(s) generated. Their gear and identity come from PBT careers.");
        }
        catch (Exception ex)
        {
            Plugin.Report("Raid profile count unavailable: " + ex.Message);
        }
    }

    private static void Status(TarkovApplication app, string text)
    {
        try
        {
            app.Matchmaker?.UpdateMatchingStatus(text);
        }
        catch { }
    }
}
