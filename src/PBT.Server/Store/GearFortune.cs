namespace PBT.Server;

// Kits follow an account's fortunes, not only its level: broke players run budget gear, rich ones overkit.
public sealed partial class PopulationStore
{
    // What a death costs, the same share of a good raid the simulation takes away.
    private double KitCost(Career c) => RaidIncome(c, Level(c.Experience), true) * .3;

    internal int FortuneTiers(Career c, bool roll)
    {
        if (!policy.WealthGearTiers) return 0;
        double kits = c.WealthRubles / Math.Max(1, KitCost(c));
        int shift = kits < .5 ? -2 : kits < 3 ? -1 : kits >= 60 ? 1 : 0;
        // Now and then anyone runs a cheap kit to save money, or goes all in.
        if (roll)
        {
            double r = Random.Shared.NextDouble();
            if (r < .12) shift--;
            else if (r > .92) shift++;
        }
        return shift;
    }

    // Everything that moves a kit away from its level's APBS tier.
    internal int TierShift(Career c, bool roll) => TierBoost(c) + FortuneTiers(c, roll);
}
