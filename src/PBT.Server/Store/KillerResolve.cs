using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace PBT.Server;

public sealed partial class PopulationStore
{
    public int? ResolveKiller(KillerLookup request, MongoId session)
    {
        lock (gate)
        {
            if (!Ready || request is null || request.Nickname is null || request.RuntimeProfileId is null
                || request.AccountId is null || request.Side is null || request.Nickname.Length > 128
                || request.RuntimeProfileId.Length > 64 || request.AccountId.Length > 32 || request.Side.Length > 16) return null;
            var owned = encounters.Values.Where(e => CanAccess(e, session)).ToArray();
            if (int.TryParse(request.AccountId, out int aid) && owned.Any(e => e.AccountId == aid)) return aid;
            var direct = owned.Where(e => e.RuntimeProfileId == request.RuntimeProfileId).ToArray();
            if (IsId(request.RuntimeProfileId) && direct.Length == 1) return direct[0].AccountId;
            var named = owned.Where(e => population.Careers.TryGetValue(e.CareerId, out var career)
                && career.Name.Equals(request.Nickname, StringComparison.OrdinalIgnoreCase)
                && career.Side.Equals(request.Side, StringComparison.OrdinalIgnoreCase)).ToArray();
            return named.Length == 1 ? named[0].AccountId : null;
        }
    }
}
