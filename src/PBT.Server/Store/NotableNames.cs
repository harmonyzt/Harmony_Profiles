using System.Text.RegularExpressions;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    // Splits "NanamiTV" or "TTV_Bob" into words, so "Acouthyt" is not read as a YouTube tag.
    private static readonly Regex NameWords = new("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

    // Gigachads and streamers (TTV, YT, TV tags) play a bit better and always bring good gear.
    internal static bool IsNotableName(string name) => name.Contains("chad", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ttv", StringComparison.OrdinalIgnoreCase)
        || NameWords.Matches(name).Any(m => m.Value.ToLowerInvariant() is "ttv" or "yt" or "tv");

    // Names never change, and the regex ran for every career on every PMC pick.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> notableCache = new();

    private bool Notable(Career c) => policy.NotableNames.Enabled
        && (notableCache.GetOrAdd(c.Name, IsNotableName) || notableCache.GetOrAdd(c.SourceName, IsNotableName)
            || ExtraStreamers().Contains(c.Name) || ExtraStreamers().Contains(c.SourceName));

    private (List<string> Source, HashSet<string> Names)? extraStreamers;

    // Rebuilt only when a saved policy brings a new list.
    private HashSet<string> ExtraStreamers()
    {
        var list = policy.NotableNames.ExtraNames;
        if (extraStreamers is not { } cached || !ReferenceEquals(cached.Source, list))
            extraStreamers = cached = (list, new HashSet<string>(list.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase));
        return cached.Names;
    }

    internal int TierBoost(Career c)
    {
        if (!Notable(c)) return 0;
        var n = policy.NotableNames;
        return n.TierBoostMinimum + (int)(Stable(c.Id + ":tier-boost") * (n.TierBoostMaximum - n.TierBoostMinimum + 1));
    }
}
