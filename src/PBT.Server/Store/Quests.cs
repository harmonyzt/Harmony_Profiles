using System.Text.Json.Nodes;

namespace PBT.Server;

// Careers work through the real trader quests in a plausible order. Only the count is stored, the list is rebuilt from it.
public sealed partial class PopulationStore
{
    private sealed record QuestNode(string Id, string Name, int Level, string[] Requires);

    private List<QuestNode>? questCatalog;

    private List<QuestNode> QuestCatalog()
    {
        if (questCatalog is not null)
            return questCatalog;
        var names = locales.GetLocaleDb();
        var raw = new Dictionary<string, (string Name, int Level, string[] Requires)>();
        foreach (var quest in templates.Quests.Values)
        {
            // Arena and event modes only.
            if (quest.GameModes is { Count: > 0 })
                continue;
            string id = quest.Id.ToString();
            var start = JsonNode.Parse(json.Serialize(quest.Conditions)!)?["AvailableForStart"]?.AsArray() ?? [];
            int level = start
                .OfType<JsonObject>()
                .Where(c => c["conditionType"]?.ToString() == "Level")
                .Select(c => (int)(c["value"]?.GetValue<double>() ?? 0))
                .DefaultIfEmpty(1)
                .Max();
            var requires = start
                .OfType<JsonObject>()
                .Where(c => c["conditionType"]?.ToString() == "Quest")
                .Select(c => c["target"]?.ToString())
                .Where(t => t is not null)
                .Select(t => t!)
                .ToArray();
            raw[id] = (names.GetValueOrDefault(id + " name") ?? quest.QuestName ?? id, Math.Max(1, level), requires);
        }

        // A quest cannot come before its chain does, so it inherits the highest level up the chain.
        var levels = new Dictionary<string, int>();

        int Effective(string id, int depth)
        {
            if (levels.TryGetValue(id, out int known))
                return known;
            if (!raw.TryGetValue(id, out var q) || depth > 64)
                return 1;
            int level = q.Requires.Where(raw.ContainsKey).Select(r => Effective(r, depth + 1)).Append(q.Level).Max();
            return levels[id] = level;
        }

        questCatalog = raw.Select(p => new QuestNode(
                p.Key,
                p.Value.Name,
                Effective(p.Key, 0),
                p.Value.Requires.Where(raw.ContainsKey).ToArray()
            ))
            .OrderBy(q => q.Level)
            .ToList();
        return questCatalog;
    }

    private double QuestDrive(Career c)
    {
        double share = c.Archetype switch
        {
            "Timmy" => .35,
            "Aggressive" => .45,
            "Survivor" => .75,
            _ => .55,
        };
        share += (Stable(c.Id + ":quests") - .5) * .3 + (Notable(c) ? .1 : 0) - (Blatant(c) ? .35 : 0);
        return Math.Clamp(share, .05, 1);
    }

    private int QuestsInReach(Career c)
    {
        int level = Level(c.Experience);
        return QuestCatalog().Count(q => q.Level <= level);
    }

    private List<QuestNode> QuestOrder(Career c, int count)
    {
        int level = Level(c.Experience);
        var open = QuestCatalog().Where(q => q.Level <= level).ToList();
        var ids = open.Select(q => q.Id).ToHashSet();
        var waiting = open.ToDictionary(q => q.Id, q => q.Requires.Count(ids.Contains));
        var unlocks = new Dictionary<string, List<QuestNode>>();
        foreach (var q in open)
        foreach (var r in q.Requires.Where(ids.Contains))
            (unlocks.TryGetValue(r, out var list) ? list : unlocks[r] = new()).Add(q);
        var ready = new PriorityQueue<QuestNode, double>();
        double Key(QuestNode q) => q.Level + Stable(c.Id + ":quest:" + q.Id) * 6;
        foreach (var q in open.Where(q => waiting[q.Id] == 0))
            ready.Enqueue(q, Key(q));
        var done = new List<QuestNode>();
        while (done.Count < count && ready.TryDequeue(out var next, out _))
        {
            done.Add(next);
            if (!unlocks.TryGetValue(next.Id, out var after))
                continue;
            foreach (var q in after)
                if (--waiting[q.Id] == 0)
                    ready.Enqueue(q, Key(q));
        }

        return done;
    }

    private void UpdateQuests(Career c, bool log)
    {
        int target = (int)Math.Round(QuestsInReach(c) * QuestDrive(c));
        // Prestige wipes quests, so the count can fall.
        if (target <= c.QuestsDone)
        {
            c.QuestsDone = target;
            return;
        }

        var done = QuestOrder(c, target);
        if (log && done.Count > c.QuestsDone)
        {
            var fresh = done.Skip(c.QuestsDone).ToList();
            string more = fresh.Count > 1 ? $" and {fresh.Count - 1} more" : "";
            Log("quest", $"{c.Name} completed {fresh[^1].Name}{more}");
        }

        c.QuestsDone = done.Count;
    }

    private (List<string> Recent, List<string> Active) QuestSummary(Career c)
    {
        var order = QuestOrder(c, c.QuestsDone + 3);
        var recent = order.Take(c.QuestsDone).Reverse().Take(5).Select(q => q.Name).ToList();
        var active = order.Skip(c.QuestsDone).Select(q => q.Name).ToList();
        return (recent, active);
    }
}
