using System;
using System.Reflection;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;

namespace HarmonyProfiles.Client;

// Visiting a hideout from the raid-end screens loads the scene before the main menu has rebuilt the player,
// and Player.InitVisuals throws on the missing camera. The load never finishes, so block it until the menu is back.
internal static class HideoutVisitGuard
{
    private const string Reason = "Return to the main menu to visit hideouts.";
    private static readonly FieldInfo HideoutButton = AccessTools.Field(typeof(OtherPlayerProfileScreen), "_hideoutButton");
    private static bool sessionEnd;
    private static DefaultUIButton? disabled;

    internal static void Install(Harmony harmony)
    {
        var load = AccessTools.Method(typeof(OtherPlayerProfileScreen), "LoadHideout", Type.EmptyTypes);
        var show = AccessTools.FirstMethod(typeof(OtherPlayerProfileScreen), m => m.Name == "Show" && m.GetParameters().Length == 4);
        var results = AccessTools.FirstMethod(typeof(SessionResultExitStatus), m => m.Name == "Show" && m.GetParameters().Length == 7);
        var menu = AccessTools.FirstMethod(typeof(MenuScreen), m => m.Name == "Show" && m.GetParameters().Length == 3);
        if (load == null || show == null || results == null || menu == null || HideoutButton?.FieldType != typeof(DefaultUIButton))
            throw new MissingMethodException("Profile hideout visit contract unavailable.");
        harmony.Patch(results, prefix: new HarmonyMethod(typeof(HideoutVisitGuard), nameof(SessionEndShown)));
        harmony.Patch(menu, postfix: new HarmonyMethod(typeof(HideoutVisitGuard), nameof(MenuShown)));
        harmony.Patch(show, postfix: new HarmonyMethod(typeof(HideoutVisitGuard), nameof(ProfileShown)));
        harmony.Patch(load, prefix: new HarmonyMethod(typeof(HideoutVisitGuard), nameof(BeforeLoad)));
    }

    private static void SessionEndShown() => sessionEnd = true;

    private static void MenuShown() => sessionEnd = false;

    private static void ProfileShown(OtherPlayerProfileScreen __instance)
    {
        try
        {
            var button = (DefaultUIButton)HideoutButton.GetValue(__instance);
            if (button == null) return;
            if (!sessionEnd)
            {
                // The screen is reused, so undo our own disable once the menu is back.
                if (ReferenceEquals(button, disabled)) { button.Interactable = true; disabled = null; }
                return;
            }
            if (!button.Interactable) return;
            button.Interactable = false;
            button.SetTooltips(Reason, Reason);
            disabled = button;
        }
        catch (Exception ex) { Plugin.Report("Hideout visit guard failed: " + ex.Message); }
    }

    private static bool BeforeLoad()
    {
        if (!sessionEnd) return true;
        Plugin.Report("Hideout visit blocked on the raid-end screens; it would never finish loading.");
        return false;
    }
}
