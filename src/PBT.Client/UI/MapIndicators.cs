using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT.UI;
using HarmonyLib;
using JsonType;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PBT.Client;

// Signal bars for each map's share of the simulated online, plus its average queue, at the top right of the map name.
internal static class MapIndicators
{
    private sealed class Stat
    {
        public double Share;
        public int QueueSeconds;
        public bool WeeklyBoss;
    }

    private const string Name = "PBT.Online";
    private static readonly Dictionary<LocationButton, string> buttons = new();
    private static Dictionary<string, Stat> stats = new(StringComparer.OrdinalIgnoreCase);
    private static float fetched = -100;
    private static Task fetching = Task.CompletedTask;
    private static volatile bool changed;

    internal static void Install(Harmony harmony)
    {
        var show = AccessTools.Method(typeof(LocationButton), nameof(LocationButton.Show));
        if (show is null || AccessTools.Field(typeof(LocationButton), "_spawnableToggle") is null)
            throw new MissingMethodException("Map selection button contract changed.");
        harmony.Patch(show, postfix: new HarmonyMethod(typeof(MapIndicators), nameof(Shown)));
    }

    private static void Shown(LocationButton __instance, LocationSettings.Location location)
    {
        try
        {
            if (__instance == null || location?.Id is null) return;
            buttons[__instance] = location.Id;
            Apply(__instance, location.Id);
            if (Time.realtimeSinceStartup - fetched > 15 && fetching.IsCompleted)
            {
                fetched = Time.realtimeSinceStartup;
                fetching = Fetch();
            }
        }
        catch (Exception ex) { Plugin.Report("Map online indicator failed: " + ex.Message); }
    }

    private static async Task Fetch()
    {
        try
        {
            var response = JObject.Parse(await RequestHandler.GetJsonAsync("/pbt/world/maps"));
            if (response.Value<int>("ProtocolVersion") != 1 || response["Maps"] is not JObject maps) return;
            var next = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps.Properties())
                next[map.Name] = new Stat
                {
                    Share = map.Value.Value<double>("Share"), QueueSeconds = map.Value.Value<int>("QueueSeconds"),
                    WeeklyBoss = map.Value.Value<bool>("WeeklyBoss")
                };
            stats = next;
            changed = true;
        }
        catch (Exception ex) { Plugin.Report("Map online fetch failed: " + ex.Message); }
    }

    // Unity objects are only touched on the main thread.
    internal static void Tick()
    {
        if (!changed) return;
        changed = false;
        foreach (var pair in buttons.ToArray())
        {
            if (pair.Key == null) { buttons.Remove(pair.Key!); continue; }
            Apply(pair.Key, pair.Value);
        }
    }

    private static void Apply(LocationButton button, string id)
    {
        var label = button._spawnableToggle != null ? button._spawnableToggle.GetComponentInChildren<TextMeshProUGUI>(true) : null;
        if (label == null) return;
        var root = label.transform.Find(Name) as RectTransform ?? Create(label);
        if (!TryStat(id, out var stat))
        {
            root.gameObject.SetActive(false);
            return;
        }
        root.gameObject.SetActive(true);
        root.anchoredPosition = new Vector2(Mathf.Min(label.preferredWidth, label.rectTransform.rect.width) / 2 + 3, -2);
        int bars = stat.Share >= .15 ? 4 : stat.Share >= .09 ? 3 : stat.Share >= .04 ? 2 : stat.Share > 0 ? 1 : 0;
        var lit = stat.WeeklyBoss ? new Color(1f, .7f, .3f, .95f) : new Color(.85f, .9f, .85f, .95f);
        for (int i = 0; i < 4; i++)
            root.GetChild(i).GetComponent<Image>().color = i < bars ? lit : new Color(1, 1, 1, .2f);
        var time = root.GetChild(4).GetComponent<TextMeshProUGUI>();
        time.text = $"{stat.QueueSeconds / 60}:{stat.QueueSeconds % 60:D2}";
    }

    // Map select has one Factory button for both times of day, so it shows them combined.
    private static bool TryStat(string id, out Stat stat)
    {
        if (!id.StartsWith("factory4", StringComparison.OrdinalIgnoreCase)) return stats.TryGetValue(id, out stat!);
        bool day = stats.TryGetValue("factory4_day", out var d), night = stats.TryGetValue("factory4_night", out var n);
        stat = !day ? n! : !night ? d : new Stat { Share = d.Share + n.Share, QueueSeconds = Math.Min(d.QueueSeconds, n.QueueSeconds), WeeklyBoss = d.WeeklyBoss || n.WeeklyBoss };
        return day || night;
    }

    private static RectTransform Create(TextMeshProUGUI label)
    {
        var root = new GameObject(Name, typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(label.transform, false);
        root.anchorMin = root.anchorMax = new Vector2(.5f, 1);
        root.pivot = new Vector2(0, 0);
        root.sizeDelta = new Vector2(44, 12);
        for (int i = 0; i < 4; i++)
        {
            var bar = new GameObject("Bar" + i, typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            bar.SetParent(root, false);
            bar.anchorMin = bar.anchorMax = bar.pivot = Vector2.zero;
            bar.anchoredPosition = new Vector2(i * 4, 0);
            bar.sizeDelta = new Vector2(3, 3 + i * 3);
            bar.GetComponent<Image>().raycastTarget = false;
        }
        var text = new GameObject("Queue", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        text.rectTransform.SetParent(root, false);
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = text.rectTransform.pivot = Vector2.zero;
        text.rectTransform.anchoredPosition = new Vector2(18, -1);
        text.rectTransform.sizeDelta = new Vector2(40, 14);
        text.font = label.font;
        text.fontSize = Mathf.Max(9, label.fontSize * .55f);
        text.color = new Color(.85f, .9f, .85f, .85f);
        text.alignment = TextAlignmentOptions.BottomLeft;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        return root;
    }
}
