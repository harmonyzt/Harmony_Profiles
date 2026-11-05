using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        // Splits "NanamiTV" or "TTV_Bob" into words, so "Acouthyt" is not read as a YouTube tag.
        private static readonly Regex NameWords = new Regex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

        // Cached, the regex ran for every career on every PMC pick.
        private static readonly ConcurrentDictionary<string, bool> notableCache = new ConcurrentDictionary<string, bool>();
        private volatile StreamerSet? extraStreamers;

        // Gigachads and streamers (TTV, YT, TV tags) play a bit better and always bring good gear.
        internal static bool IsNotableName(string name)
        {
            return name.Contains("chad", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ttv", StringComparison.OrdinalIgnoreCase)
                                                                             || NameWords.Matches(name).Any(m => m.Value.ToLowerInvariant() is "ttv" or "yt" or "tv");
        }

        private bool Notable(Career c)
        {
            return policy.NotableNames.Enabled && (notableCache.GetOrAdd(c.Name, IsNotableName) || notableCache.GetOrAdd(c.SourceName, IsNotableName)
                                                                                                || ExtraStreamers().Contains(c.Name) || ExtraStreamers().Contains(c.SourceName));
        }

        // One reference swap, since raid spawns read it outside the gate.
        private HashSet<string> ExtraStreamers()
        {
            List<string> list = policy.NotableNames.ExtraNames;
            StreamerSet? cached = extraStreamers;
            if (cached is null || !ReferenceEquals(cached.Source, list))
                extraStreamers = cached = new StreamerSet(list, new HashSet<string>(list.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase));
            return cached.Names;
        }

        internal int TierBoost(Career c)
        {
            if (IsCheater(c)) return policy.Cheaters.TierBoost;
            if (!Notable(c)) return 0;
            NotableNameSettings n = policy.NotableNames;
            return n.TierBoostMinimum + (int)(Stable(c.Id + ":tier-boost") * (n.TierBoostMaximum - n.TierBoostMinimum + 1));
        }

        private sealed record StreamerSet(List<string> Source, HashSet<string> Names);
    }
}
