using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PBT.Client.Cheaters;
using PBT.Client.UI;
using SPT.Common.Http;

namespace PBT.Client;

[BepInPlugin("com.local.pbt.client", "PBT", "1.0.0")]
[BepInIncompatibility("com.local.profiles.client")]
[BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
// Loads Fika first so the Fika check in Awake can see it.
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
// Cheater behaviour runs as a BigBrain layer. Without it cheaters still wear their kit but play normally.
[BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    private Harmony? harmony;
    private Harmony? activityHarmony;
    private static ManualLogSource? log;
    private static bool compatible;
    private static string raidId = "";
    private static bool captured;
    private static Task pendingUpload = Task.CompletedTask;
    private static string pendingAccount = "";
    private static string pendingProfile = "";
    internal static string KillerProfileId => pendingProfile;
    internal static string KillerAccount => pendingAccount;

    internal static bool CheatReports { get; private set; }

    // The Fika headless has no main player, but it hosts every raid it runs.
    private static bool headless;

    // Fika clients see bots as observed players with no AI brain, flagged by this field instead.
    private static AccessTools.FieldRef<object, bool>? observedAi;
    private static System.Type? fikaPlayer;

    internal static void Report(string message) => log?.LogWarning(message);

    internal static void Trace(string message) => log?.LogInfo(message);

    private void Awake()
    {
        log = Logger;
        try
        {
            var killed = AccessTools.Method(typeof(Player), nameof(Player.OnBeenKilledByAggressor));
            var start = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
            var view = AccessTools.Method(
                typeof(ClientBackendSession),
                nameof(ClientBackendSession.GetOtherPlayerProfile),
                new[] { typeof(string) }
            );
            if (
                killed is null
                || start is null
                || view?.ReturnType != typeof(Task<Result<OtherPlayerProfileDescriptor>>)
                || Array.Find(killed.GetParameters(), p => p.Name == "aggressor")?.ParameterType != typeof(IPlayer)
            )
                throw new MissingMethodException("SPT 4.1 killer or raid-start hook unavailable.");
            harmony = new Harmony("com.local.pbt.client");
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(Plugin), nameof(Started)));
            harmony.Patch(killed, prefix: new HarmonyMethod(typeof(Plugin), nameof(Killed)));
            harmony.Patch(view, prefix: new HarmonyMethod(typeof(Plugin), nameof(View)));
            DeathProfileUi.Install(harmony);
            HideoutAccessGate.Install(harmony);
            try
            {
                HideoutVisitGuard.Install(harmony);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Hideout visit guard disabled. " + ex.Message);
            }

            try
            {
                MapIndicators.Install(harmony);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Map online indicators disabled. " + ex.Message);
            }

            try
            {
                // Fika replaces game creation, so hook its version instead.
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
                {
                    Matching.InstallFika(harmony);
                    Logger.LogInfo("Matching simulation hooked into Fika (host only).");
                }
                else
                    Matching.Install(harmony);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Matching simulation disabled. " + ex.Message);
            }

            try
            {
                Connection.Install(harmony);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Connection simulation fight detection disabled. " + ex.Message);
            }

            try
            {
                CheaterRoster.Install(harmony, Config);
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("xyz.drakia.bigbrain"))
                {
                    RegisterCheaterBrain();
                    CheaterRoster.BrainInstalled = true;
                }
                else
                    Logger.LogInfo("BigBrain not found; cheaters wear their kit but play like normal bots.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Cheater behaviour disabled. " + ex.Message);
            }

            activityHarmony = new Harmony("com.local.pbt.activity");
            try
            {
                RaidActivity.Install(activityHarmony);
            }
            catch (Exception ex)
            {
                activityHarmony.UnpatchSelf();
                activityHarmony = null;
                Logger.LogWarning("Raid observation disabled; killer capture remains available. " + ex.Message);
            }

            headless = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.headless");
            Connection.Fika = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core");
            Connection.Allowed = !headless;
            fikaPlayer = AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer");
            if (fikaPlayer is not null && AccessTools.Field(fikaPlayer, "IsObservedAI")?.FieldType == typeof(bool))
                observedAi = AccessTools.FieldRefAccess<bool>(fikaPlayer, "IsObservedAI");
            _ = CheckServer();
        }
        catch (Exception ex)
        {
            harmony?.UnpatchSelf();
            activityHarmony?.UnpatchSelf();
            Logger.LogError("PBT capture disabled; native raid behavior retained. " + ex);
        }
    }

    // Separate so the BigBrain types only load when BigBrain is there.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RegisterCheaterBrain() => CheaterBrain.Register();

    private static async Task CheckServer()
    {
        try
        {
            var status = JObject.Parse(await RequestHandler.GetJsonAsync("/pbt/status"));
            compatible = status.Value<int>("ProtocolVersion") == 1 && status.Value<bool>("Ready");
            RaidActivity.Supported = compatible && RaidActivity.Installed && status.Value<int>("ActivityProtocolVersion") == 1;
            CheatReports = compatible && status.Value<bool?>("CheatReports") == true;
            if (!compatible)
                log?.LogWarning("PBT server is unavailable or incompatible; capture disabled.");
            else
                log?.LogInfo("Connected to the PBT server.");
        }
        catch (Exception ex)
        {
            compatible = false;
            log?.LogWarning("PBT server check failed: " + ex.Message);
        }
    }

    private static void Started(GameWorld __instance)
    {
        if (!headless && __instance.MainPlayer?.IsYourPlayer != true)
            return;
        raidId = Guid.NewGuid().ToString("N");
        captured = false;
        try
        {
            Matching.RaidStarted();
        }
        catch (Exception ex)
        {
            Report("Raid profile count failed: " + ex.Message);
        }

        CheaterRoster.RaidStarted(raidId);
        try
        {
            RaidActivity.Start(__instance, raidId);
        }
        catch (Exception ex)
        {
            Report("Raid observation startup failed: " + ex.Message);
        }

        pendingAccount = "";
        pendingProfile = "";
        pendingUpload = Task.CompletedTask;
        if (!compatible)
            _ = CheckServer();
    }

    private static void Killed(Player __instance, IPlayer aggressor, bool ___AggressorFound)
    {
        if (compatible)
        {
            try
            {
                RaidActivity.Killed(__instance, aggressor, ___AggressorFound);
            }
            catch (Exception ex)
            {
                Report("Raid death observation failed: " + ex.Message);
            }
        }

        if (
            !compatible
            || captured
            || raidId.Length == 0
            || !__instance.IsYourPlayer
            || ___AggressorFound
            || aggressor is not Player killer
            || !IsBot(killer)
            || killer.ProfileId == __instance.ProfileId
            || killer.Profile.Side is not (EPlayerSide.Bear or EPlayerSide.Usec)
        )
            return;
        try
        {
            // Never serialize live Unity item objects directly.
            var descriptor = new ProfileDescriptor(killer.Profile, VisualsOnlySearchController.Instance);
            var profile = descriptor.ToUnparsedData(Array.Empty<JsonConverter>()).JObject;
            if (
                profile["Inventory"] is not JObject inventory
                || inventory["equipment"] is not JValue equipmentId
                || inventory["items"] is not JArray items
            )
                throw new InvalidOperationException("Native descriptor did not provide equipment and items.");
            if (!int.TryParse(killer.Profile.AccountId, out int accountId))
                throw new InvalidOperationException("Killer account ID is not numeric.");
            var payload = new JObject
            {
                ["ProtocolVersion"] = 1,
                ["RaidId"] = raidId,
                ["RuntimeProfileId"] = killer.ProfileId,
                ["AccountId"] = accountId,
                ["Equipment"] = EquipmentTree(equipmentId, items),
                ["Customization"] = profile["Customization"]?.DeepClone() ?? new JObject(),
            };

            // Finish all access to game objects on this thread; the asynchronous upload owns only text.
            string body = payload.ToString(Formatting.None);
            captured = true;
            pendingAccount = accountId.ToString();
            pendingProfile = killer.ProfileId;
            pendingUpload = Upload(body);
        }
        catch (Exception ex)
        {
            log?.LogWarning("PBT killer capture failed; generated loadout remains available. " + ex);
        }
    }

    internal static bool IsBot(Player player)
    {
        if (player.IsAI)
            return true;
        try
        {
            return observedAi is not null && fikaPlayer!.IsInstanceOfType(player) && observedAi(player);
        }
        catch
        {
            return false;
        }
    }

    private static JObject EquipmentTree(JValue equipmentId, JArray items)
    {
        string root = equipmentId.Value<string>() ?? throw new InvalidOperationException("Missing equipment root.");
        var included = new System.Collections.Generic.HashSet<string> { root };
        var selected = new JArray();
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items)
            {
                string? id = item.Value<string>("_id");
                string? parent = item.Value<string>("parentId");
                if (id != null && parent != null && included.Contains(parent) && included.Add(id))
                    changed = true;
            }
        } while (changed);

        foreach (var item in items)
            if (included.Contains(item.Value<string>("_id") ?? ""))
                selected.Add(item.DeepClone());
        return new JObject { ["Id"] = root, ["Items"] = selected };
    }

    private static bool View(ClientBackendSession __instance, string __0, ref Task<Result<OtherPlayerProfileDescriptor>> __result)
    {
        Task upload = pendingUpload;
        if (__0 != pendingAccount || upload.IsCompleted)
            return true;
        __result = ViewAfterUpload(__instance, __0, upload);
        return false;
    }

    private static async Task<Result<OtherPlayerProfileDescriptor>> ViewAfterUpload(
        ClientBackendSession session,
        string account,
        Task upload
    )
    {
        await upload;
        return await session.GetOtherPlayerProfile(account);
    }

    private static async Task Upload(string body)
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.PostJsonAsync("/pbt/killer", body));
            if (response.Value<int>("ProtocolVersion") != 1 || !response.Value<bool>("Accepted"))
                log?.LogWarning("PBT server rejected the killer snapshot; generated loadout remains available.");
            else
                log?.LogInfo("PBT killer equipment saved.");
        }
        catch (Exception ex)
        {
            log?.LogWarning("PBT killer upload failed; generated loadout remains available. " + ex.Message);
        }
    }

    private void Update()
    {
        try
        {
            RaidActivity.Tick();
        }
        catch (Exception ex)
        {
            Report("Raid observation checkpoint failed: " + ex.Message);
        }

        try
        {
            MapIndicators.Tick();
        }
        catch (Exception ex)
        {
            Report("Map online indicator update failed: " + ex.Message);
        }

        try
        {
            CheaterRoster.Tick();
        }
        catch (Exception ex)
        {
            Report("Cheater update failed: " + ex.Message);
        }

        try
        {
            Connection.Tick();
        }
        catch (Exception ex)
        {
            Report("Connection simulation failed: " + ex.Message);
        }
    }

    // After the game's own movement and camera, so a frozen or snapped-back player stays put this frame.
    private void LateUpdate()
    {
        try
        {
            Connection.Frame();
        }
        catch (Exception ex)
        {
            Report("Connection movement failed: " + ex.Message);
        }
    }

    private void OnDestroy()
    {
        RaidActivity.End();
        RaidActivity.Supported = false;
        activityHarmony?.UnpatchSelf();
        compatible = false;
        raidId = "";
        harmony?.UnpatchSelf();
    }
}
