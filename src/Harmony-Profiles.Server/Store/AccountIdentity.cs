using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace HarmonyProfiles.Server;

public sealed partial class PopulationStore
{
    private static readonly string[] editions = ["Standard", "Left Behind", "Prepare To Escape", "Edge Of Darkness", "Unheard"];

    private void EnsureAccountIdentity(Career career)
    {
        if (career.Edition.Length > 0) return;
        // Stable account identity, separate from member badges and fictional progression.
        double roll = Stable(career.Id + ":edition");
        string edition = roll < .38 ? editions[0] : roll < .48 ? editions[1] : roll < .58 ? editions[2]
            : roll < .83 ? editions[3] : editions[4];
        if (!templates.Profiles.TryGetValue(edition, out var profile))
            throw new InvalidDataException("Required native account edition is missing: " + edition);
        var info = (career.Side == "Bear" ? profile.Bear : profile.Usec)?.Character?.Info
            ?? throw new InvalidDataException("Native account edition has no PMC Info: " + edition);
        career.Edition = edition;
        career.GameVersion = info.GameVersion ?? throw new InvalidDataException("Native edition has no game version.");
        career.MemberCategory = (int)(info.MemberCategory ?? MemberCategory.Default);
        career.SelectedMemberCategory = (int)(info.SelectedMemberCategory ?? MemberCategory.Default);
        double badge = Stable(career.Id + ":member-badge");
        int special = badge < .002 ? (int)MemberCategory.Developer : badge < .012 ? (int)MemberCategory.Sherpa
            : badge < .017 ? (int)MemberCategory.Emissary : 0;
        if (special != 0) { career.MemberCategory |= special; career.SelectedMemberCategory = special; }
        dirty = true;
    }

    internal static void ApplyAccountIdentity(Career career, Info info)
    {
        info.GameVersion = career.GameVersion;
        info.MemberCategory = (MemberCategory)career.MemberCategory;
        info.SelectedMemberCategory = (MemberCategory)career.SelectedMemberCategory;
        info.Level = PopulationStore.Active!.Level(career.Experience);
        info.Experience = career.Experience;
        info.PrestigeLevel = career.PrestigeLevel;
        info.RegistrationDate = (int)Math.Clamp(career.RegisteredUtc, 1, int.MaxValue);
    }
}
