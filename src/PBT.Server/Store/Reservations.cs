using SPTarkov.Server.Core.Models.Common;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    private readonly Dictionary<string, HashSet<string>> reservedCareers = new();

    public void ResetReservations(MongoId session)
    {
        lock (gate)
        {
            reservedCareers.Remove(session.ToString());
            ClearLobby(session.ToString());
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            population.SessionEndedUtc[session.ToString()] = now;
            // Not seen in this raid yet. The client's last report can still arrive, so the world tick releases them in a minute and a half.
            foreach (var e in encounters.Values.Where(e => e.SessionId == session.ToString() && now - e.GeneratedUtc < 10800
                && !population.RaidObservations.ContainsKey(e.AccountId) && !population.PendingStories.ContainsKey(e.AccountId)))
            {
                if (population.Careers.TryGetValue(e.CareerId, out var c) && activities.TryGetValue(c.Id, out var a) && a.Held)
                    a.Until = Math.Min(a.Until, now + 90);
            }
            dirty = true;
        }
    }
}
