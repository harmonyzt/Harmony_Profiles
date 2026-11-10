namespace PBT.Server;

// Real players have a time of day they play and sessions that last hours, not a coin flip every twenty minutes.
public sealed partial class PopulationStore
{
    private sealed record Schedule(string Name, int Share, double[] Hours, double Weekday, double Weekend)
    {
        // Mean weight over a week, so every schedule plays the same hours per day overall.
        public double Mean { get; } = (Hours.Average() * Weekday * 5 + Hours.Average() * Weekend * 2) / 7;
    }

    private sealed record ScheduleSet(PeakHourSettings Settings, Schedule[] Schedules);

    private ScheduleSet? scheduleSet;

    // Rebuilt only when the saved peak settings are replaced
    private Schedule[] Schedules
    {
        get
        {
            var peak = policy.World.Peak;
            var cached = scheduleSet;
            if (cached is not null && ReferenceEquals(cached.Settings, peak))
                return cached.Schedules;
            var built = new ScheduleSet(peak, BuildSchedules(peak));
            scheduleSet = built;
            return built.Schedules;
        }
    }

    private static Schedule[] BuildSchedules(PeakHourSettings peak)
    {
        var result = new List<Schedule>();
        foreach (var source in peak.Schedules)
        {
            if (source.Share <= 0 || source.Hours is not { Length: 24 } || (source.Weekday <= 0 && source.Weekend <= 0))
                continue;
            var hours = new double[24];
            for (int h = 0; h < 24; h++)
            {
                int from = ((h - peak.ShiftHours) % 24 + 24) % 24;
                hours[h] = Math.Max(0, 1 + (source.Hours[from] - 1) * peak.Strength);
            }

            if (hours.Sum() <= 0)
                continue;
            result.Add(new Schedule(source.Name, source.Share, hours, source.Weekday, source.Weekend * peak.WeekendBoost));
        }

        // Validation guarantees a usable schedule, but keep a flat fallback so a bad hand-edited file never stops play.
        return result.Count > 0 ? result.ToArray() : [new Schedule("Anytime", 1, Enumerable.Repeat(1d, 24).ToArray(), 1, 1)];
    }

    private Schedule ScheduleOf(Career c)
    {
        var schedules = Schedules;
        double roll = Stable(c.Id + ":schedule") * schedules.Sum(s => s.Share);
        foreach (var s in schedules)
        {
            roll -= s.Share;
            if (roll < 0)
                return s;
        }

        return schedules[^1];
    }

    private double ScheduleWeight(Career c, DateTime local)
    {
        if (!policy.World.PeakHours)
            return 1;
        var s = ScheduleOf(c);
        bool weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return s.Hours[local.Hour] * (weekend ? s.Weekend : s.Weekday) / s.Mean;
    }

    private double DailyOnlineHours(Career c)
    {
        bool friend = population.Friendships.Values.Any(links => links.TryGetValue(c.Id, out var f) && f.Accepted && !f.Removed);
        return Math.Min(
            20,
            c.DailyRaidHours * OnlineFactor(c) * settings.OnlineRateMultiplier * (friend ? settings.FriendActivityMultiplier : 1)
        );
    }

    private static double SessionHours(Career c) => Math.Clamp(.6 + c.DailyRaidHours * .55, .75, 5);

    private long RollSessionEnd(Career c, long now, bool midway = false)
    {
        double hours = SessionHours(c) * (.5 + Random.Shared.NextDouble() * 1.1);
        if (DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            hours *= 1.3;
        if (midway)
            hours *= Random.Shared.NextDouble();
        return now + (long)(Math.Max(.25, hours) * 3600);
    }

    // Chance an offline career starts a session within the next check.
    private double SessionStartChance(Career c, double intervalSeconds)
    {
        if (c.Retired || c.DailyRaidHours <= 0)
            return 0;
        double daily = DailyOnlineHours(c),
            share = Math.Min(.85, daily / 24);
        double perDay = daily / SessionHours(c);
        double rate = perDay * ScheduleWeight(c, DateTime.Now) * intervalSeconds / 86400 / Math.Max(.15, 1 - share);
        return Math.Clamp(rate, 0, .9);
    }

    private bool RollOnline(Career c)
    {
        if (c.Retired || c.DailyRaidHours <= 0)
            return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (c.IsOnline && c.SessionEndUtc > now)
            return true;
        bool online = Random.Shared.NextDouble() < Math.Min(.85, DailyOnlineHours(c) / 24 * ScheduleWeight(c, DateTime.Now));
        if (online)
            c.SessionEndUtc = RollSessionEnd(c, now, midway: true);
        return online;
    }

    private bool SessionOver(Career c, long now) => now >= c.SessionEndUtc;

    private bool Tilted(Career c) => DateTimeOffset.UtcNow.ToUnixTimeSeconds() < c.TiltUntilUtc;

    // How badly losing gets to someone. Survivors shrug, aggressive players boil over.
    private double Temper(Career c) =>
        (.5 + Stable(c.Id + ":temper"))
        * c.Archetype switch
        {
            "Aggressive" => 1.5,
            "Timmy" => 1.2,
            "Survivor" => .6,
            _ => 1,
        };

    // After a death: rage quit, tilt into reckless cheap-kit raids, or shrug it off.
    private bool AfterDeath(Career c, long now)
    {
        c.DeathStreak++;
        if (c.DeathStreak < 2)
            return false;
        double temper = Temper(c);
        double quit = Math.Min(.7, .07 * Math.Pow(c.DeathStreak - 1, 1.4) * temper);
        if (Random.Shared.NextDouble() < quit)
        {
            c.SessionEndUtc = now;
            c.TiltUntilUtc = 0;
            Log("ragequit", $"{c.Name} rage quit after {c.DeathStreak} deaths in a row");
            return true;
        }

        if (c.DeathStreak >= 3 && now >= c.TiltUntilUtc && Random.Shared.NextDouble() < Math.Min(.8, .25 * temper))
        {
            c.TiltUntilUtc = now + Random.Shared.Next(2400, 7201);
            // A tilted player keeps queueing instead of logging off on time.
            c.SessionEndUtc = Math.Max(c.SessionEndUtc, c.TiltUntilUtc);
            if (Random.Shared.NextDouble() < .3)
                Log("tilt", $"{c.Name} is on tilt");
        }

        return false;
    }

    private static void AfterSurvival(Career c)
    {
        c.DeathStreak = 0;
        if (Random.Shared.NextDouble() < .6)
            c.TiltUntilUtc = 0;
    }

    private string Mood(Career c) =>
        Tilted(c) ? "Tilted"
        : c.DeathStreak >= 2 ? "Frustrated"
        : c.CurrentSurvivalStreak >= 5 ? "On a roll"
        : "";
}
