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

    private bool Notable(Career c) => policy.NotableNames.Enabled && (IsNotableName(c.Name) || IsNotableName(c.SourceName));

    internal int TierBoost(Career c)
    {
        if (!Notable(c)) return 0;
        var n = policy.NotableNames;
        return n.TierBoostMinimum + (int)(Stable(c.Id + ":tier-boost") * (n.TierBoostMaximum - n.TierBoostMinimum + 1));
    }
}
