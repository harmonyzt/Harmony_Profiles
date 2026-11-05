namespace PBT.Server
{
    // Real players have a time of day they play and sessions that last hours, not a coin flip every twenty minutes.
    public sealed partial class PopulationStore
    {
        private static readonly Schedule[] Schedules =
        [
            new Schedule("Evenings", 40, Curve((0, .5), (2, .1), (8, .05), (16, .5), (19, 2.2), (22, 2), (23, 1.2)), 1, 1.25),
            new Schedule("Night owl", 15, Curve((0, 2.2), (3, 1.8), (6, .2), (12, .1), (19, .6), (22, 1.6)), 1, 1.1),
            new Schedule("Afternoons", 15, Curve((0, .1), (9, .2), (13, 1.8), (17, 2), (20, .8), (23, .2)), 1, 1.1),
            new Schedule("Weekends", 15, Curve((0, .8), (3, .2), (9, .4), (13, 1.5), (18, 2), (23, 1.2)), .35, 2.6),
            new Schedule("Mornings", 5, Curve((0, .05), (6, .6), (8, 2), (12, 1.6), (15, .4), (20, .1)), 1, 1),
            new Schedule("Anytime", 10, Curve((0, .8), (6, .5), (12, 1), (18, 1.3)), 1, 1.1)
        ];

        // Weights per hour, interpolated between points and wrapping past midnight. The first point is hour 0.
        private static double[] Curve(params (int Hour, double Weight)[] points)
        {
            double[] hours = new double[24];
            for (int h = 0; h < 24; h++)
            {
                int i = points.Length - 1;
                while (points[i].Hour > h)
                {
                    i--;
                }

                (int h0, double w0) = points[i];
                (int h1, double w1) = i + 1 < points.Length ? points[i + 1] : (24, points[0].Weight);
                hours[h] = w0 + (w1 - w0) * (h - h0) / (h1 - h0);
            }

            return hours;
        }

        private static Schedule ScheduleOf(Career c)
        {
            double roll = Stable(c.Id + ":schedule") * Schedules.Sum(s => s.Share);
            foreach (Schedule s in Schedules)
            {
                roll -= s.Share;
                if (roll < 0) return s;
            }

            return Schedules[^1];
        }

        private double ScheduleWeight(Career c, DateTime local)
        {
            if (!policy.World.PeakHours) return 1;
            Schedule s = ScheduleOf(c);
            bool weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            return s.Hours[local.Hour] * (weekend ? s.Weekend : s.Weekday) / s.Mean;
        }

        private double DailyOnlineHours(Career c)
        {
            bool friend = population.Friendships.Values.Any(links => links.TryGetValue(c.Id, out Friendship? f) && f.Accepted && !f.Removed);
            return Math.Min(20, c.DailyRaidHours * OnlineFactor(c) * settings.OnlineRateMultiplier * (friend ? settings.FriendActivityMultiplier : 1));
        }

        private static double SessionHours(Career c)
        {
            return Math.Clamp(.6 + c.DailyRaidHours * .55, .75, 5);
        }

        private long RollSessionEnd(Career c, long now, bool midway = false)
        {
            double hours = SessionHours(c) * (.5 + Random.Shared.NextDouble() * 1.1);
            if (DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) hours *= 1.3;
            if (midway) hours *= Random.Shared.NextDouble();
            return now + (long)(Math.Max(.25, hours) * 3600);
        }

        // Chance an offline career starts a session within the next check.
        private double SessionStartChance(Career c, double intervalSeconds)
        {
            if (c.Retired || c.DailyRaidHours <= 0) return 0;
            double daily = DailyOnlineHours(c), share = Math.Min(.85, daily / 24);
            double perDay = daily / SessionHours(c);
            double rate = perDay * ScheduleWeight(c, DateTime.Now) * intervalSeconds / 86400 / Math.Max(.15, 1 - share);
            return Math.Clamp(rate, 0, .9);
        }

        private bool RollOnline(Career c)
        {
            if (c.Retired || c.DailyRaidHours <= 0) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (c.IsOnline && c.SessionEndUtc > now) return true;
            bool online = Random.Shared.NextDouble() < Math.Min(.85, DailyOnlineHours(c) / 24 * ScheduleWeight(c, DateTime.Now));
            if (online) c.SessionEndUtc = RollSessionEnd(c, now, true);
            return online;
        }

        private bool SessionOver(Career c, long now)
        {
            return now >= c.SessionEndUtc;
        }


        private bool Tilted(Career c)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < c.TiltUntilUtc;
        }

        // How badly losing gets to someone. Survivors shrug, aggressive players boil over.
        private double Temper(Career c)
        {
            return (.5 + Stable(c.Id + ":temper")) * c.Archetype switch
            {
                "Aggressive" => 1.5,
                "Timmy" => 1.2,
                "Survivor" => .6,
                _ => 1
            };
        }

        // After a death: rage quit, tilt into reckless cheap-kit raids, or shrug it off.
        private bool AfterDeath(Career c, long now)
        {
            c.DeathStreak++;
            if (c.DeathStreak < 2) return false;
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
                if (Random.Shared.NextDouble() < .3) Log("tilt", $"{c.Name} is on tilt");
            }

            return false;
        }

        private static void AfterSurvival(Career c)
        {
            c.DeathStreak = 0;
            if (Random.Shared.NextDouble() < .6) c.TiltUntilUtc = 0;
        }

        private string Mood(Career c)
        {
            return Tilted(c) ? "Tilted" : c.DeathStreak >= 2 ? "Frustrated" : c.CurrentSurvivalStreak >= 5 ? "On a roll" : "";
        }

        private sealed record Schedule(string Name, int Share, double[] Hours, double Weekday, double Weekend)
        {
            // Mean weight over a week, so every schedule plays the same hours per day overall.
            public double Mean { get; } = (Hours.Average() * Weekday * 5 + Hours.Average() * Weekend * 2) / 7;
        }
    }
}
