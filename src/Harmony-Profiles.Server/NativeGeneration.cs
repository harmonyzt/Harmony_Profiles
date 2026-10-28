using SPTarkov.Server.Core.Models.Common;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private readonly Dictionary<string, HashSet<string>> reservedCareers = new();

    public void ResetReservations(MongoId session)
    {
        lock (gate)
        {
            reservedCareers.Remove(session.ToString());
            population.SessionEndedUtc[session.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            dirty = true;
        }
    }
}
