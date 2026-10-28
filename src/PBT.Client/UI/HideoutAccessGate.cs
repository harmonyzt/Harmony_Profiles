using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Hideout;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace PBT.Client;

public sealed class HideoutAccessGate : MonoBehaviour
{
    private static readonly FieldInfo Rear = AccessTools.Field(typeof(HideoutScreenOverlay), "_hideoutRear");
    private static readonly FieldInfo Owner = AccessTools.Field(typeof(HideoutScreenRear), "_playerOwner");
    private static readonly FieldInfo EnterButton = AccessTools.Field(typeof(HideoutScreenOverlay), "_enterHideoutButtonSpawner");
    private static readonly FieldInfo EnabledTip = AccessTools.Field(typeof(DefaultUIButton), "_enabledTooltip");
    private static readonly FieldInfo DisabledTip = AccessTools.Field(typeof(DefaultUIButton), "_disabledTooltip");
    private static readonly MethodInfo Enter = AccessTools.Method(typeof(HideoutScreenOverlay), "EnterHideoutMode", Type.EmptyTypes);
    private HideoutScreenOverlay? overlay;
    private HideoutPlayerOwner? owner;
    private DefaultUIButton? button;
    private string account = "", enabledTip = "", disabledTip = "";
    private bool interactable, pending, replaying;
    private int revision;

    internal static void Install(Harmony harmony)
    {
        var show = AccessTools.Method(typeof(HideoutScreenOverlay), "Show", new[]
            { typeof(HideoutPlayerOwner), typeof(bool), typeof(IEftSession), typeof(AreaData[]), typeof(HideoutScreenRear) });
        if (show == null || Enter?.ReturnType != typeof(void) || Rear?.FieldType != typeof(HideoutScreenRear)
            || Owner?.FieldType != typeof(HideoutPlayerOwner) || EnterButton?.FieldType != typeof(DefaultUIButton)
            || EnabledTip?.FieldType != typeof(string) || DisabledTip?.FieldType != typeof(string))
            throw new MissingMemberException("Native Hideout entrance contract unavailable.");
        harmony.Patch(show, prefix: new HarmonyMethod(typeof(HideoutAccessGate), nameof(BeforeShow)),
            postfix: new HarmonyMethod(typeof(HideoutAccessGate), nameof(AfterShow)));
        harmony.Patch(Enter, prefix: new HarmonyMethod(typeof(HideoutAccessGate), nameof(BeforeEnter)));
    }

    private static HideoutPlayerOwner? Guest(HideoutScreenOverlay screen)
    {
        var rear = Rear.GetValue(screen) as HideoutScreenRear;
        var guest = rear != null ? Owner.GetValue(rear) as HideoutPlayerOwner : null;
        return guest != null && guest.IsGuest && int.TryParse(guest.HideoutOwnerAccountId, out int aid)
            && aid >= 1_500_000_000 && aid < 2_000_000_000 ? guest : null;
    }

    private static void BeforeShow(HideoutScreenOverlay __instance) => __instance.GetComponent<HideoutAccessGate>()?.Restore();

    private static void AfterShow(HideoutScreenOverlay __instance)
    {
        try
        {
            var guest = Guest(__instance);
            if (guest == null) return;
            var gate = __instance.GetComponent<HideoutAccessGate>() ?? __instance.gameObject.AddComponent<HideoutAccessGate>();
            gate.Bind(__instance, guest);
            gate.StartCoroutine(gate.Check(false));
        }
        catch (Exception ex) { Plugin.Report("Hideout access initialization failed: " + ex.Message); }
    }

    private static bool BeforeEnter(HideoutScreenOverlay __instance)
    {
        var guest = Guest(__instance);
        if (guest == null) return true;
        try
        {
            var gate = __instance.GetComponent<HideoutAccessGate>() ?? __instance.gameObject.AddComponent<HideoutAccessGate>();
            if (gate.replaying && ReferenceEquals(gate.owner, guest) && gate.account == guest.HideoutOwnerAccountId) return true;
            if (!ReferenceEquals(gate.owner, guest) || gate.account != guest.HideoutOwnerAccountId) gate.Bind(__instance, guest);
            if (!gate.pending) gate.StartCoroutine(gate.Check(true));
        }
        catch (Exception ex) { Plugin.Report("Hideout entrance denied: " + ex.Message); }
        return false;
    }

    private void Bind(HideoutScreenOverlay screen, HideoutPlayerOwner guest)
    {
        Restore();
        overlay = screen; owner = guest; account = guest.HideoutOwnerAccountId;
        button = (DefaultUIButton)EnterButton.GetValue(screen);
        interactable = button.Interactable;
        enabledTip = (string)EnabledTip.GetValue(button) ?? "";
        disabledTip = (string)DisabledTip.GetValue(button) ?? "";
        button.Interactable = false;
        button.SetTooltips(enabledTip, "Checking Hideout friendship...");
    }

    private IEnumerator Check(bool enter)
    {
        int current = ++revision;
        pending = true;
        Task<string>? request = null;
        try
        {
            request = RequestHandler.PostJsonAsync("/pbt/hideout/access",
                JsonConvert.SerializeObject(new { AccountId = account }));
            // Observe a late fault even when the screen closes before the HTTP request finishes.
            _ = request.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { Plugin.Report("Hideout friendship check failed: " + ex.Message); }
        float deadline = Time.realtimeSinceStartup + 15;
        while (request != null && !request.IsCompleted && Time.realtimeSinceStartup < deadline)
        {
            if (current != revision) yield break;
            yield return null;
        }
        if (current != revision) yield break;
        pending = false;
        if (overlay == null || owner == null || !overlay.gameObject.activeInHierarchy
            || !ReferenceEquals(Guest(overlay), owner) || owner.HideoutOwnerAccountId != account) yield break;
        bool allowed = false;
        string reason = "Friendship could not be checked. Reopen the overview to retry.";
        try
        {
            if (request?.Status == TaskStatus.RanToCompletion)
            {
                var result = JObject.Parse(request.Result);
                allowed = result.Value<int>("ProtocolVersion") == 1 && result.Value<bool>("Managed") && result.Value<bool>("CanEnter");
                reason = result.Value<string>("Reason") ?? reason;
            }
        }
        catch (Exception ex) { Plugin.Report("Hideout friendship response invalid: " + ex.Message); }
        if (button != null)
        {
            button.Interactable = allowed && interactable;
            button.SetTooltips(allowed ? enabledTip : reason, reason);
        }
        if (!allowed || !enter || !interactable) yield break;
        replaying = true;
        try { Enter.Invoke(overlay, null); }
        catch (Exception ex) { Plugin.Report("Hideout entrance failed: " + ex.GetBaseException().Message); }
        finally { replaying = false; }
    }

    private void Restore()
    {
        revision++; pending = false; replaying = false;
        if (button != null) { button.Interactable = interactable; button.SetTooltips(enabledTip, disabledTip); }
        overlay = null; owner = null; button = null; account = "";
    }

    private void OnDisable() { revision++; pending = false; }
    private void OnDestroy() => Restore();
}
