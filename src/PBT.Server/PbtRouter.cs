using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Utils;

namespace PBT.Server;

[Injectable(TypePriority = OnLoadOrder.Routers + 20)]
public sealed class PbtRouter(JsonUtil json, PopulationStore store) : StaticRouter(json, [
    new RouteAction<HideoutAccessRequest>("/pbt/hideout/access",
        (url, request, session, output, token) => new ValueTask<string>(json.Serialize(store.HideoutAccess(request, session))!)),
    new RouteAction<KillerLookup>("/pbt/killer/resolve", (url, request, session, output, token) => new ValueTask<string>(json.Serialize(new
    {
        AccountId = store.ResolveKiller(request, session)
    })!)),
    new RouteAction<ActivityReport>("/pbt/activity", (url, request, session, output, token) =>
    {
        try
        {
            return new ValueTask<string>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Accepted = store.Observe(request, session)
            })!);
        }
        catch (Exception ex)
        {
            store.Report(ex);
            return new ValueTask<string>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Accepted = 0
            })!);
        }
    }),
    new RouteAction<KillerReport>("/pbt/killer", (url, request, session, output, token) =>
    {
        try
        {
            return new ValueTask<string>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Accepted = store.Capture(request, session)
            })!);
        }
        catch (Exception ex)
        {
            store.Report(ex);
            return new ValueTask<string>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Accepted = false
            })!);
        }
    }),
    new RouteAction<MatchingRequest>("/pbt/world/matching", (url, request, session, output, token) =>
    {
        (int Seconds, int Looking, int Matched, MatchedServer? Server, bool Wait, int Target) matching = (0, 0, 0, null, false, 0);
        try
        {
            matching = store.Matching(request.Map, session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<string>(json.Serialize(new
        {
            ProtocolVersion = 1,
            matching.Seconds,
            matching.Looking,
            matching.Matched,
            matching.Server,
            matching.Wait,
            matching.Target
        })!);
    }),
    new RouteAction("/pbt/raid/cheaters", (url, request, session, output, token) =>
    {
        (bool Behaviour, List<int> Cheaters, CheaterPlaySettings Play) raid = (false, [], new CheaterPlaySettings());
        try
        {
            raid = store.RaidCheaters(session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<object>(json.Serialize(new
        {
            ProtocolVersion = 1,
            raid.Behaviour,
            raid.Cheaters,
            raid.Play
        })!);
    }),
    new RouteAction<CheaterBanRequest>("/pbt/raid/banned", (url, request, session, output, token) =>
    {
        bool banned = false;
        try
        {
            banned = store.BannedInRaid(request, session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<string>(json.Serialize(new
        {
            ProtocolVersion = 1,
            Banned = banned
        })!);
    }),
    new RouteAction<MatchingRequest>("/pbt/world/matching/poll", (url, request, session, output, token) =>
    {
        (int Found, int Target, bool Done, int Looking, long ElapsedSeconds) poll = (0, 0, true, 0, 0);
        try
        {
            poll = store.PollMatching(session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<string>(json.Serialize(new
        {
            ProtocolVersion = 1,
            poll.Found,
            poll.Target,
            poll.Done,
            poll.Looking,
            poll.ElapsedSeconds
        })!);
    }),
    new RouteAction<MatchingRequest>("/pbt/world/matching/host", (url, request, session, output, token) =>
    {
        (int Seconds, int Looking, long ElapsedMs) host = (0, 0, 0);
        try
        {
            host = store.HostMatching(request.Map);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<string>(json.Serialize(new
        {
            ProtocolVersion = 1,
            host.Seconds,
            host.Looking,
            host.ElapsedMs
        })!);
    }),
    new RouteAction("/pbt/world/raid", (url, request, session, output, token) =>
    {
        int loaded = 0;
        try
        {
            loaded = store.RaidCareers(session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<object>(json.Serialize(new
        {
            ProtocolVersion = 1,
            Loaded = loaded
        })!);
    }),
    new RouteAction("/pbt/world/maps", (url, request, session, output, token) =>
    {
        try
        {
            return new ValueTask<object>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Maps = store.MapIndicators()
            })!);
        }
        catch (Exception ex)
        {
            store.Report(ex);
            return new ValueTask<object>(json.Serialize(new
            {
                ProtocolVersion = 1,
                Maps = new
                {
                }
            })!);
        }
    }),
    new RouteAction<EndLocalRaidRequestData>("/client/match/local/end", (url, request, session, output, token) =>
    {
        store.ResetReservations(session);
        store.RecordRaidLoss(request, session);
        store.RecordRaidOutcome(request, session);
        return new ValueTask<string>(output ?? "");
    }),
    new RouteAction<CheatReportRequest>("/client/report/send", (url, request, session, output, token) =>
    {
        try
        {
            store.ReportCheater(request, session);
        }
        catch (Exception ex)
        {
            store.Report(ex);
        }

        return new ValueTask<string>(output ?? "");
    }),
    new RouteAction("/pbt/status", (url, request, session, output, token) => new ValueTask<object>(json.Serialize(new
    {
        ProtocolVersion = 1,
        ActivityProtocolVersion = 1,
        HideoutAccessProtocolVersion = 1,
        Ready = store.Ready,
        GenerationMode = store.GenerationMode,
        ApbsMatched = store.ApbsMatched,
        ApbsFallback = store.ApbsFallback,
        CheatReports = store.CheatReports
    })!))
])
{
}
