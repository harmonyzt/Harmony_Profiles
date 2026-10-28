using System.Globalization;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly HashSet<string> occupiedNicknames = new(StringComparer.Ordinal);
    private Dictionary<string, Career> bySourceName = new();

    private static string SourceName(Career career) => string.IsNullOrWhiteSpace(career.SourceName)
        ? career.Name : career.SourceName;

    private void InitializeIdentities()
    {
        occupiedNicknames.Clear();
        foreach (var career in population.Careers.Values)
            career.SourceName = SourceName(career);
        var groups = population.Careers.Values.GroupBy(c => NameKey("", c.Name)).ToArray();
        // Real players keep their names. A name pool can contain one (BotCallsigns has "user"), so that career moves aside.
        var players = saves.GetProfiles().Values.Select(p => p.CharacterData?.PmcData?.Info?.Nickname)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => NameKey("", n!)).ToHashSet();
        occupiedNicknames.UnionWith(players);
        // Reserve every existing public name before allocating suffixes. Retired accounts keep theirs.
        foreach (var group in groups) occupiedNicknames.Add(group.Key);
        int changed = 0;
        foreach (var group in groups)
            foreach (var career in group.OrderBy(c => c.AccountId).Skip(players.Contains(group.Key) ? 0 : 1))
            {
                career.Name = AllocateNickname(career.SourceName);
                changed++;
            }
        if (changed > 0)
            logger.Info($"[Harmony-Profiles] Assigned unique nicknames to {changed} colliding careers; accounts and histories retained.");
    }

    private string AllocateNickname(string source)
    {
        if (occupiedNicknames.Add(NameKey("", source))) return source;
        // Only generated collision names are shortened. Existing unique mod names stay intact.
        for (int index = 2; index < int.MaxValue; index++)
        {
            string suffix = "_" + index.ToString(CultureInfo.InvariantCulture);
            int elements = Math.Max(1, 15 - suffix.Length);
            var text = new StringInfo(source);
            string stem = text.LengthInTextElements > elements ? text.SubstringByTextElements(0, elements) : source;
            string candidate = stem + suffix;
            if (occupiedNicknames.Add(NameKey("", candidate))) return candidate;
        }
        throw new InvalidDataException("No unique nickname available.");
    }

    private void IndexIdentities()
    {
        byName = population.Careers.Values.ToDictionary(c => NameKey(c.Side, c.Name));
        bySourceName = population.Careers.Values.ToDictionary(c => NameKey(c.Side, c.SourceName));
    }
}
