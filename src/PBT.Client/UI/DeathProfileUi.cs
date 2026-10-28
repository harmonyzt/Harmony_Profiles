using System;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;

namespace PBT.Client;

internal static class DeathProfileUi
{
    internal static void Install(Harmony harmony)
    {
        var show = AccessTools.Method(typeof(SessionResultExitStatus), nameof(SessionResultExitStatus.Show),
            new[] { typeof(Profile), typeof(PlayerVisualRepresentation), typeof(ESideType), typeof(ExitStatus),
                typeof(TimeSpan), typeof(IEftSession), typeof(bool) });
        if (show is null) throw new MissingMethodException("Death-screen Show method unavailable.");
        harmony.Patch(show, prefix: new HarmonyMethod(typeof(DeathProfileUi), nameof(BeforeShown)), postfix: new HarmonyMethod(typeof(DeathProfileUi), nameof(Shown)) { priority = Priority.Last });
        KillerProfileMenu.CheckContract();
        var interaction = AccessTools.Method(typeof(ReportPanel), "UpdateCanvasGroupInteraction", Type.EmptyTypes)
            ?? throw new MissingMethodException("Native report-menu interaction method unavailable.");
        harmony.Patch(interaction, postfix: new HarmonyMethod(typeof(DeathProfileUi), nameof(ReportsInteractionUpdated)));
        var lifetime = AccessTools.Method(typeof(OverallStatsPanel), "SetOverallLifetime", Type.EmptyTypes);
        if (lifetime is null) throw new MissingMethodException("Account lifetime renderer unavailable.");
        harmony.Patch(lifetime, postfix: new HarmonyMethod(typeof(DeathProfileUi), nameof(LifetimeShown)));
    }

    private static void BeforeShown(ReportPanel ____reportPanel)
    {
        if (____reportPanel == null) return;
        try
        {
            var menu = ____reportPanel.GetComponent<KillerProfileMenu>();
            if (menu == null || !menu.Bound) return;
            menu.ResetBinding();
            ____reportPanel.Close();
        }
        catch (Exception ex) { Plugin.Report("Previous killer menu cleanup failed: " + ex.Message); }
    }

    private static void Shown(SessionResultExitStatus __instance, Profile __0, ExitStatus __3, IEftSession __5,
        ReportPanel ____reportPanel, EFT.Hideout.ComplementaryButton ____showProfileButton)
    {
        try
        {
            var aggressor = __0.EftStats?.Aggressor;
            if (__3 != ExitStatus.Killed || aggressor == null || ____reportPanel == null
                || aggressor.Side is not (EPlayerSide.Bear or EPlayerSide.Usec)) return;
            var menu = ____reportPanel.GetComponent<KillerProfileMenu>() ?? ____reportPanel.gameObject.AddComponent<KillerProfileMenu>();
            menu.Bind(____reportPanel, ____showProfileButton, __5, aggressor.AccountId ?? "",
                aggressor.ProfileId ?? "", aggressor.Name ?? "", aggressor.Side.ToString());
            Plugin.Trace($"Killer menu bound: account={aggressor.AccountId}, runtime={aggressor.ProfileId}, nativeReports={__5.ReportAvailable}.");
        }
        catch (Exception ex)
        {
            if (____reportPanel != null) ____reportPanel.GetComponent<KillerProfileMenu>()?.ResetBinding();
            Plugin.Report("Killer profile menu unavailable: " + ex);
        }
    }

    private static void ReportsInteractionUpdated(ReportPanel __instance)
    {
        var menu = __instance.GetComponent<KillerProfileMenu>();
        if (menu != null) menu.EnableMenuInteraction();
    }

    private static void LifetimeShown(EFT.Counters.CountersCollection ____pmcStats, int ____registrationDate,
        bool ____selfStats, LocalizedText ____overallLifetimeLabel)
    {
        try
        {
            if (____selfStats || ____registrationDate <= 0 || ____pmcStats == null
                || ____pmcStats.GetAllInt(new object[] { "PbtSynthetic" }) != 1) return;
            double seconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)____registrationDate);
            if (seconds < 86400)
            {
                ____overallLifetimeLabel.LocalizationKey = "{0}h";
                ____overallLifetimeLabel.SetFormatValues(new object[] { (int)(seconds / 3600) });
            }
            else
            {
                ____overallLifetimeLabel.LocalizationKey = "{0}d";
                ____overallLifetimeLabel.SetFormatValues(new object[] { (int)(seconds / 86400) });
            }
        }
        catch (Exception ex) { Plugin.Report("Account lifetime unavailable: " + ex.Message); }
    }
}

