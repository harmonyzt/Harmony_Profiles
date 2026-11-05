using System.Text.Json;

namespace PBT.Server;

public sealed record WatchedPlayers(List<WorldPlayerRow> Players, List<WorldEvent> Events);

// Kept in Data, so a wipe clears them with the careers.
public sealed partial class PopulationStore
{
    private HashSet<string>? favorites;

    private string FavoritesPath => System.IO.Path.Combine(directory, "favorites.json");

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

        return favorites ??= new();
    }

    public HashSet<string> GetFavorites()
    {
        lock (gate) return new(Favorites());
    }

    public void SetFavorite(string id, bool favorite)
    {
        lock (gate)
        {
            if (!Ready || !population.Careers.ContainsKey(id)) return;
            var set = Favorites();
            if (!(favorite ? set.Add(id) : set.Remove(id))) return;
            set.RemoveWhere(f => !population.Careers.TryGetValue(f, out var c) || c.Retired);
            AtomicFile.Write(FavoritesPath, JsonSerializer.Serialize(set), durable: false, backup: false);
        }
    }

    public WatchedPlayers GetWatched(int events)
    {
        lock (gate)
        {
            if (!Ready) throw new InvalidOperationException("PBT has not finished loading.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var observed = ObservedActiveCareers(now);
            var careers = Favorites().Select(id => population.Careers.GetValueOrDefault(id)).Where(c => c is { Retired: false }).Select(c => c!).ToList();
            var players = careers.Select(c => PlayerRow(c, now, observed)).ToList();
            var log = careers.Count == 0 ? [] : worldLog.Where(e => careers.Any(c => MentionsName(e.Text, c.Name))).Take(events).ToList();
            return new(players, log);
        }
    }
}
