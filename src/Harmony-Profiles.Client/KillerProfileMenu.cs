using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HarmonyProfiles.Client;

public sealed class KillerProfileMenu : MonoBehaviour
{
    private static readonly FieldInfo Reports = AccessTools.Field(typeof(ReportPanel), "_availableReports");
    private static readonly FieldInfo Buttons = AccessTools.Field(typeof(ReportPanel), "_reportButtons");
    private static readonly FieldInfo Dropdown = AccessTools.Field(typeof(ReportPanel), "_reportDropdownPanel");
    private static readonly FieldInfo Trigger = AccessTools.Field(typeof(ReportPanel), "_reportDropdownButton");
    private static readonly FieldInfo Canvas = AccessTools.Field(typeof(ReportPanel), "_canvasGroup");
    private static readonly FieldInfo Open = AccessTools.Field(typeof(ReportPanel), "_isOpen");
    private static readonly MethodInfo Toggle = AccessTools.Method(typeof(ReportPanel), "ChangeReportButtonsShowState", Type.EmptyTypes);
    private readonly List<RectState> rects = new();
    private readonly List<(GameObject Row, bool Active)> originalRows = new();
    private ReportPanel? panel;
    private ComplementaryButton? standalone;
    private bool standaloneActive;
    private GameObject? entry, dropdown;
    private RectTransform? content;
    private Button? profileButton;
    private TMP_Text? label;
    private CanvasGroup? canvas;
    private object? availableReports;
    private string account = "", runtime = "", nickname = "", side = "";
    private bool opening;
    private int revision, laidOutRows = -1;
    internal bool Bound { get; private set; }

    internal static void CheckContract()
    {
        if (Reports == null || Buttons == null || Dropdown?.FieldType != typeof(GameObject)
            || Trigger?.FieldType != typeof(Button) || Canvas?.FieldType != typeof(CanvasGroup)
            || Open?.FieldType != typeof(bool) || Toggle == null)
            throw new MissingMemberException("Native killer dropdown contract unavailable.");
    }

    internal void Bind(ReportPanel owner, ComplementaryButton nativeProfile, IEftSession session,
        string accountId, string profileId, string name, string faction)
    {
        ResetBinding();
        panel = owner;
        standalone = nativeProfile;
        standaloneActive = standalone != null && standalone.gameObject.activeSelf;
        account = accountId;
        if (string.IsNullOrEmpty(account) && profileId == Plugin.KillerProfileId) account = Plugin.KillerAccount;
        runtime = profileId; nickname = name; side = faction;
        availableReports = Reports.GetValue(owner);
        dropdown = (GameObject)Dropdown.GetValue(owner);
        canvas = (CanvasGroup)Canvas.GetValue(owner);
        Bound = true;

        var buttons = (IDictionary)Buttons.GetValue(owner);
        GameObject? prototype = null;
        foreach (DictionaryEntry item in buttons)
        {
            var type = item.Value.GetType();
            var button = AccessTools.Field(type, "Button").GetValue(item.Value) as Button;
            var group = AccessTools.Field(type, "CanvasGroup").GetValue(item.Value) as CanvasGroup;
            if (button == null || group == null || !button.transform.IsChildOf(dropdown.transform)) continue;
            var row = group.transform.IsChildOf(dropdown.transform) && group.transform != dropdown.transform
                ? group.gameObject : button.gameObject;
            if (originalRows.Any(r => r.Row == row)) continue;
            originalRows.Add((row, row.activeSelf));
            prototype ??= row;
        }
        if (prototype == null) throw new MissingMemberException("Native report-menu row template unavailable.");
        content = prototype.transform.parent as RectTransform;
        if (content == null) throw new MissingMemberException("Native report-menu content unavailable.");

        // Show the native affordance without enabling report RPCs in an offline SPT session.
        if (!session.ReportAvailable)
        {
            Reports.SetValue(owner, Enum.ToObject(Reports.FieldType, 0));
            owner.Show(session, account);
        }
        owner.gameObject.SetActive(true);
        ((Button)Trigger.GetValue(owner)).gameObject.SetActive(true);
        Open.SetValue(owner, false);
        dropdown.SetActive(false);

        entry = Instantiate(prototype, content, false);
        entry.name = "HarmonyProfilesViewProfileMenuItem";
        // Keep the native background behind all actions. FirstSibling draws the backdrop over our text.
        entry.transform.SetSiblingIndex(originalRows.Min(r => r.Row.transform.GetSiblingIndex()));
        profileButton = entry.GetComponentInChildren<Button>(true);
        if (profileButton == null) throw new MissingMemberException("Native menu row has no button.");
        profileButton.onClick = new Button.ButtonClickedEvent();
        profileButton.onClick.AddListener(OpenProfile);
        foreach (var localized in entry.GetComponentsInChildren<LocalizedText>(true)) localized.LocalizationKey = "WatchProfile";
        label = entry.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = "WatchProfile".Localized();
        entry.SetActive(true);
        foreach (var group in entry.GetComponentsInChildren<CanvasGroup>(true))
        { group.alpha = 1; group.interactable = true; group.blocksRaycasts = true; }
        profileButton.interactable = true;
        profileButton.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        if (standalone != null) standalone.Close();
        EnableMenuInteraction();
        LayoutMenu();
    }

    internal void EnableMenuInteraction()
    {
        // Native logic only counts report rows. The profile action remains available independently.
        if (!Bound || entry == null || canvas == null) return;
        canvas.alpha = 1;
        canvas.interactable = true;
        canvas.blocksRaycasts = true;
    }

    private void SaveRect(RectTransform rect)
    {
        if (rects.All(r => r.Rect != rect)) rects.Add(new RectState(rect));
    }

    private void LayoutMenu()
    {
        if (entry == null || dropdown == null || content == null) return;
        var rows = originalRows.Where(r => r.Row != null && r.Row.activeSelf).Select(r => r.Row)
            .OrderBy(r => r.transform.GetSiblingIndex()).Prepend(entry).ToArray();
        laidOutRows = rows.Length;
        var sample = entry.transform as RectTransform;
        if (sample == null) return;
        float height = LayoutUtility.GetPreferredHeight(sample);
        if (height <= 0) height = sample.rect.height;
        if (height <= 0) height = label != null ? label.fontSize + 12 : 30;

        if (content.GetComponent<LayoutGroup>() == null)
        {
            for (int index = 0; index < rows.Length; index++)
            {
                var rect = rows[index].transform as RectTransform;
                if (rect == null || rect.parent != content) continue;
                if (rows[index] != entry) SaveRect(rect);
                rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, 1);
                rect.offsetMin = new Vector2(0, -(index + 1) * height);
                rect.offsetMax = new Vector2(0, -index * height);
            }
            SaveRect(content);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height * rows.Length);
        }
        else
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            float preferred = LayoutUtility.GetPreferredHeight(content);
            if (preferred > 0) height = preferred / rows.Length;
        }
        if (dropdown.transform is RectTransform background)
        {
            SaveRect(background);
            background.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height * rows.Length);
        }
    }

    private void LateUpdate()
    {
        if (!Bound || dropdown == null || !dropdown.activeSelf) return;
        if (1 + originalRows.Count(r => r.Row != null && r.Row.activeSelf) != laidOutRows) LayoutMenu();
    }

    private void CloseDropdown()
    {
        if (panel != null && (bool)Open.GetValue(panel)) Toggle.Invoke(panel, null);
    }

    private async void OpenProfile()
    {
        if (!Bound || opening) return;
        opening = true;
        int currentRevision = revision;
        string fallback = account;
        if (profileButton != null) profileButton.interactable = false;
        CloseDropdown();
        try
        {
            var request = new JObject { ["AccountId"] = account, ["RuntimeProfileId"] = runtime, ["Nickname"] = nickname, ["Side"] = side };
            var resolved = JObject.Parse(await SPT.Common.Http.RequestHandler.PostJsonAsync(
                "/harmony-profiles/killer/resolve", request.ToString(Formatting.None)));
            if (this == null || !isActiveAndEnabled || revision != currentRevision) return;
            string? target = resolved.Value<int?>("AccountId")?.ToString();
            // Preserve ordinary native/Fika account viewing when the killer is a real player.
            if (target == null && long.TryParse(fallback, out long realAccount) && realAccount > 0
                && (realAccount < 1500000000 || realAccount >= 2000000000)) target = fallback;
            if (target == null)
            {
                Plugin.Report("Killer profile could not be resolved to a unique server encounter.");
                if (label != null) label.text = "Profile unavailable";
                return;
            }
            await ItemUiContext.Instance.ShowPlayerProfileScreen(target, EItemViewType.OtherPlayerProfileSimple);
        }
        catch (Exception ex) { Plugin.Report("Could not open killer profile: " + ex.Message); }
        finally
        {
            if (this != null && revision == currentRevision)
            {
                opening = false;
                if (profileButton != null) profileButton.interactable = true;
            }
        }
    }

    internal void ResetBinding()
    {
        revision++;
        opening = false;
        Bound = false;
        if (panel != null)
        {
            CloseDropdown();
            if (availableReports != null) Reports.SetValue(panel, availableReports);
        }
        if (entry != null) { entry.SetActive(false); Destroy(entry); }
        entry = null; profileButton = null; label = null;
        foreach (var rect in rects) rect.Restore();
        rects.Clear();
        foreach (var row in originalRows) if (row.Row != null) row.Row.SetActive(row.Active);
        originalRows.Clear();
        if (standalone != null) standalone.gameObject.SetActive(standaloneActive);
        panel = null; standalone = null; canvas = null; content = null; dropdown = null; availableReports = null;
        account = runtime = nickname = side = "";
        laidOutRows = -1;
    }

    private void OnDisable()
    {
        // A profile overlay can temporarily disable the parent. Keep its binding until the next Show.
        revision++;
        opening = false;
        if (profileButton != null) profileButton.interactable = true;
        CloseDropdown();
    }

    private void OnDestroy() => ResetBinding();

    private sealed class RectState
    {
        internal readonly RectTransform Rect;
        private readonly Vector2 anchorMin, anchorMax, pivot, size, position;
        private readonly int sibling;
        internal RectState(RectTransform rect)
        {
            Rect = rect; anchorMin = rect.anchorMin; anchorMax = rect.anchorMax; pivot = rect.pivot;
            size = rect.sizeDelta; position = rect.anchoredPosition; sibling = rect.GetSiblingIndex();
        }
        internal void Restore()
        {
            if (Rect == null) return;
            Rect.anchorMin = anchorMin; Rect.anchorMax = anchorMax; Rect.pivot = pivot;
            Rect.sizeDelta = size; Rect.anchoredPosition = position; Rect.SetSiblingIndex(sibling);
        }
    }
}
