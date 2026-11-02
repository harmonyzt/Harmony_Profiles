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
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            population.SessionEndedUtc[session.ToString()] = now;
            // Generated for this raid but never seen in it, so there is no story to wait for.
            foreach (var e in encounters.Values.Where(e => e.SessionId == session.ToString() && now - e.GeneratedUtc < 10800
                && !population.RaidObservations.ContainsKey(e.AccountId) && !population.PendingStories.ContainsKey(e.AccountId)))
            {
                if (population.Careers.TryGetValue(e.CareerId, out var c) && activities.TryGetValue(c.Id, out var a) && a.Held && !IsObservedActive(c, now))
                    ToMenu(c, a, now);
            }
            dirty = true;
        }
    }
}
