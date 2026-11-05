using System.Text.Json;

namespace PBT.Server
{
    public sealed record WatchedPlayers(List<WorldPlayerRow> Players, List<WorldEvent> Events);

// Kept in Data, so a wipe clears them with the careers.
    public sealed partial class PopulationStore
    {
        private HashSet<string>? favorites;

        private string FavoritesPath => Path.Combine(directory, "favorites.json");

        private HashSet<string> Favorites()
        {
            if (favorites is not null) return favorites;
            try
            {
                favorites = File.Exists(FavoritesPath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesPath)) : null;
            }
            catch
            {
            }

            return favorites ??= new HashSet<string>();
        }

        public HashSet<string> GetFavorites()
        {
            lock (gate)
            {
                return new HashSet<string>(Favorites());
            }
        }

        public void SetFavorite(string id, bool favorite)
        {
            lock (gate)
            {
                if (!Ready || !population.Careers.ContainsKey(id)) return;
                HashSet<string> set = Favorites();
                if (!(favorite ? set.Add(id) : set.Remove(id))) return;
                set.RemoveWhere(f => !population.Careers.TryGetValue(f, out Career? c) || c.Retired);
                AtomicFile.Write(FavoritesPath, JsonSerializer.Serialize(set), false, false);
            }
        }

        public WatchedPlayers GetWatched(int events)
        {
            lock (gate)
            {
                if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                HashSet<string> observed = ObservedActiveCareers(now);
                List<Career> careers = Favorites().Select(id => population.Careers.GetValueOrDefault(id)).Where(c => c is { Retired: false }).Select(c => c!).ToList();
                List<WorldPlayerRow> players = careers.Select(c => PlayerRow(c, now, observed)).ToList();
                List<WorldEvent> log = careers.Count == 0 ? [] : worldLog.Where(e => careers.Any(c => MentionsName(e.Text, c.Name))).Take(events).ToList();
                return new WatchedPlayers(players, log);
            }
        }
    }
}
