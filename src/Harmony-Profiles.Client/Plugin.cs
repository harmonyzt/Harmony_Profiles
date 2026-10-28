using System;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using EFT;
using Comfort.Common;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace HarmonyProfiles.Client;

[BepInPlugin("com.local.harmony-profiles.client", "Harmony-Profiles", "0.14.0")]
[BepInIncompatibility("com.local.profiles.client")]
[BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
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
    internal static void Report(string message) => log?.LogWarning(message);
    internal static void Trace(string message) => log?.LogInfo(message);

    private void Awake()
    {
        log = Logger;
        try
        {
            var killed = AccessTools.Method(typeof(Player), nameof(Player.OnBeenKilledByAggressor));
            var start = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
            var view = AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.GetOtherPlayerProfile), new[] { typeof(string) });
            if (killed is null || start is null
                || view?.ReturnType != typeof(Task<Result<OtherPlayerProfileDescriptor>>) || Array.Find(killed.GetParameters(), p => p.Name == "aggressor")?.ParameterType != typeof(IPlayer))
                throw new MissingMethodException("SPT 4.1 killer or raid-start hook unavailable.");
            harmony = new Harmony("com.local.harmony-profiles.client");
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(Plugin), nameof(Started)));
            harmony.Patch(killed, prefix: new HarmonyMethod(typeof(Plugin), nameof(Killed)));
            harmony.Patch(view, prefix: new HarmonyMethod(typeof(Plugin), nameof(View)));
            DeathProfileUi.Install(harmony);
            HideoutAccessGate.Install(harmony);
            activityHarmony = new Harmony("com.local.harmony-profiles.activity");
            try { RaidActivity.Install(activityHarmony); }
            catch (Exception ex)
            {
                activityHarmony.UnpatchSelf();
                activityHarmony = null;
                Logger.LogWarning("Raid observation disabled; killer capture remains available. " + ex.Message);
            }
            _ = CheckServer();
        }
        catch (Exception ex)
        {
            harmony?.UnpatchSelf();
            activityHarmony?.UnpatchSelf();
            Logger.LogError("Harmony-Profiles capture disabled; native raid behavior retained. " + ex);
        }
    }

    private static async Task CheckServer()
    {
        try
        {
            var status = JObject.Parse(await RequestHandler.GetJsonAsync("/harmony-profiles/status"));
            compatible = status.Value<int>("ProtocolVersion") == 1 && status.Value<bool>("Ready");
            RaidActivity.Supported = compatible && RaidActivity.Installed && status.Value<int>("ActivityProtocolVersion") == 1;
            if (!compatible) log?.LogWarning("Harmony-Profiles server is unavailable or incompatible; capture disabled.");
            else log?.LogInfo("Harmony-Profiles protocol 1 connected.");
        }
        catch (Exception ex) { compatible = false; log?.LogWarning("Harmony-Profiles server check failed: " + ex.Message); }
    }

    private static void Started(GameWorld __instance)
    {
        if (__instance.MainPlayer?.IsYourPlayer != true) return;
        raidId = Guid.NewGuid().ToString("N");
        captured = false;
        try { RaidActivity.Start(__instance, raidId); }
        catch (Exception ex) { Report("Raid observation startup failed: " + ex.Message); }
        pendingAccount = "";
        pendingProfile = "";
        pendingUpload = Task.CompletedTask;
        if (!compatible) _ = CheckServer();
    }

    private static void Killed(Player __instance, IPlayer aggressor, bool ___AggressorFound)
    {
        if (compatible)
        {
            try { RaidActivity.Killed(__instance, aggressor, ___AggressorFound); }
            catch (Exception ex) { Report("Raid death observation failed: " + ex.Message); }
        }
        if (!compatible || captured || raidId.Length == 0 || !__instance.IsYourPlayer || ___AggressorFound
            || aggressor is not Player killer || !killer.IsAI
            || killer.ProfileId == __instance.ProfileId
            || killer.Profile.Side is not (EPlayerSide.Bear or EPlayerSide.Usec)) return;
        try
        {
            // ProfileDescriptor owns the native item serialization. Never serialize live Unity item objects directly.
            var descriptor = new ProfileDescriptor(killer.Profile, VisualsOnlySearchController.Instance);
            var profile = descriptor.ToUnparsedData(Array.Empty<JsonConverter>()).JObject;
            if (profile["Inventory"] is not JObject inventory
                || inventory["equipment"] is not JValue equipmentId || inventory["items"] is not JArray items)
                throw new InvalidOperationException("Native descriptor did not provide equipment and items.");
            if (!int.TryParse(killer.Profile.AccountId, out int accountId))
                throw new InvalidOperationException("Killer account ID is not numeric.");
            var payload = new JObject
            {
                ["ProtocolVersion"] = 1, ["RaidId"] = raidId,
                ["RuntimeProfileId"] = killer.ProfileId,
                ["AccountId"] = accountId,
                ["Equipment"] = EquipmentTree(equipmentId, items),
                ["Customization"] = profile["Customization"]?.DeepClone() ?? new JObject()
            };
            // Finish all access to game objects on this thread; the asynchronous upload owns only text.
            string body = payload.ToString(Formatting.None);
            captured = true;
            pendingAccount = accountId.ToString();
            pendingProfile = killer.ProfileId;
            pendingUpload = Upload(body);
        }
        catch (Exception ex) { log?.LogWarning("Harmony-Profiles killer capture failed; generated loadout remains available. " + ex); }
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
                if (id != null && parent != null && included.Contains(parent) && included.Add(id)) changed = true;
            }
        } while (changed);
        foreach (var item in items)
            if (included.Contains(item.Value<string>("_id") ?? "")) selected.Add(item.DeepClone());
        return new JObject { ["Id"] = root, ["Items"] = selected };
    }

    private static bool View(ClientBackendSession __instance, string __0,
        ref Task<Result<OtherPlayerProfileDescriptor>> __result)
    {
        Task upload = pendingUpload;
        if (__0 != pendingAccount || upload.IsCompleted) return true;
        __result = ViewAfterUpload(__instance, __0, upload);
        return false;
    }

    private static async Task<Result<OtherPlayerProfileDescriptor>> ViewAfterUpload(
        ClientBackendSession session, string account, Task upload)
    {
        await upload;
        // The upload is now complete, so the prefix permits this native request.
        return await session.GetOtherPlayerProfile(account);
    }

    private static async Task Upload(string body)
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.PostJsonAsync("/harmony-profiles/killer", body));
            if (response.Value<int>("ProtocolVersion") != 1 || !response.Value<bool>("Accepted"))
                log?.LogWarning("Harmony-Profiles server rejected the killer snapshot; generated loadout remains available.");
            else log?.LogInfo("Harmony-Profiles killer equipment saved.");
        }
        catch (Exception ex) { log?.LogWarning("Harmony-Profiles killer upload failed; generated loadout remains available. " + ex.Message); }
    }

    private void Update()
    {
        try { RaidActivity.Tick(); }
        catch (Exception ex) { Report("Raid observation checkpoint failed: " + ex.Message); }
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

