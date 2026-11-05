using System.Globalization;
using System.Text;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private static readonly char[] VariantTail = "0123456789-_. ".ToCharArray();
        private readonly HashSet<string> occupiedNicknames = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<string, Career> byBaseName = new Dictionary<string, Career>();
        private Dictionary<string, Career> bySourceName = new Dictionary<string, Career>();

        // RamRanch5-0 and RamRanch6-0 read as the same player, so names compare without trailing numbers.
        // Short stems ("555", "Bo1") keep the whole name.
        private static string BaseName(string name)
        {
            string trimmed = name.Normalize(NormalizationForm.FormC).TrimEnd(VariantTail);
            return (trimmed.Length >= 3 ? trimmed : name).ToUpperInvariant();
        }

        // Caller holds the gate. The most played keeps a base name, the rest retire and leave SPT's name pool.
        private void RetireNameVariants(IEnumerable<(string Side, List<string> Names)> pools)
        {
            List<Career> variants = population.Careers.Values.Where(c => !c.Retired).GroupBy(c => NameKey(c.Side, BaseName(c.SourceName))).Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderByDescending(c => c.Raids + c.Observed.Raids).ThenBy(c => c.AccountId).Skip(1)).ToList();
            if (variants.Count == 0) return;
            foreach (Career c in variants)
            {
                c.Retired = true;
            }

            HashSet<string> dropped = variants.Select(c => NameKey(c.Side, c.SourceName)).ToHashSet();
            foreach ((string side, List<string> names) in pools)
            {
                names.RemoveAll(n => dropped.Contains(NameKey(side, n)));
            }

            dirty = true;
            logger.Info($"[PBT] Retired {variants.Count} careers whose names only differ by trailing numbers.");
        }

        private static string SourceName(Career career)
        {
            return string.IsNullOrWhiteSpace(career.SourceName) ? career.Name : career.SourceName;
        }

        private void InitializeIdentities()
        {
            occupiedNicknames.Clear();
            foreach (Career career in population.Careers.Values)
            {
                career.SourceName = SourceName(career);
            }

            IGrouping<string, Career>[] groups = population.Careers.Values.GroupBy(c => NameKey("", c.Name)).ToArray();
            // Real players keep their names. A name pool can contain one (BotCallsigns has "user"), so that career moves aside.
            var players = saves.GetProfiles().Values.Select(p => p.CharacterData?.PmcData?.Info?.Nickname).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => NameKey("", n!))
                .ToHashSet();
            occupiedNicknames.UnionWith(players);
            foreach (IGrouping<string, Career> group in groups)
            {
                occupiedNicknames.Add(group.Key);
            }

            int changed = 0;
            foreach (IGrouping<string, Career> group in groups)
            foreach (Career career in group.OrderBy(c => c.AccountId).Skip(players.Contains(group.Key) ? 0 : 1))
            {
                career.Name = AllocateNickname(career.SourceName);
                changed++;
            }

            if (changed > 0)
                logger.Info($"[PBT] Assigned unique nicknames to {changed} colliding careers; accounts and histories retained.");
        }

        private string AllocateNickname(string source)
        {
            if (occupiedNicknames.Add(NameKey("", source))) return source;
            for (int index = 2; index < int.MaxValue; index++)
            {
                string suffix = "_" + index.ToString(CultureInfo.InvariantCulture);
                int elements = Math.Max(1, 15 - suffix.Length);
                StringInfo text = new StringInfo(source);
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
            byBaseName = population.Careers.Values.Where(c => !c.Retired).GroupBy(c => NameKey(c.Side, BaseName(c.SourceName))).ToDictionary(g => g.Key, g => g.First());
        }
    }
}
