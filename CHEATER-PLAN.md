# Cheater careers (plan)

## Shape
A cheater is a normal career with `Archetype = "Cheater"`. Names come from the usual pool
(`NewCareer` / `AllocateNickname`), so nothing in the name gives it away.

## Model (`Models/Population.cs`)
- `Career`: add `long CheaterSinceUtc`, `long BannedUtc`, `string BanReason`.
- `Population`: add `Dictionary<string, CheaterReport> Reports` keyed by report id.
- `CheaterReport { CareerId, SessionId, RaidId, ReportedUtc, DueUtc, Resolved, JsonObject LostGear }`.

## Policy (`Models/Policies.cs`)
`Cheaters` block: `Enabled`, `Share` (around .01 of active careers), `BanChance` (.75),
`BanDelayHours` min/max (24-72), `FalseReportCooldownHours`, `RewardMode`
(`LostGear` | `Roubles` | `Both`), `HelmetPool` (Tagilla welding masks, Killa Maska plus
Altyn/Rys/Vulkan), `MaxStatScale`.

## Spawning
- Population upkeep converts careers to `Cheater` until `Share` is met. It prefers new
  or low-level accounts, since the meme is a fresh account that is suddenly a god.
- New "cheater" personality template: high `MinimumSurvival` (.8-.95), long daily hours
  and short raids.
- `Progression.Aggression` gives about 1.6 and `Skill()` returns about .98, so kills,
  survival and K/D all come out absurd and still agree with each other.
- Bans replace cheaters slowly, so they stay rare.

## Loadout (`Apbs/RaidLoadout.cs` / `GearFortune.cs`)
- Cheaters get a big `TierShift` boost (top APBS tier no matter their level), which matches
  "level 12 in full meta".
- After APBS builds the kit, swap the headwear for a `HelmetPool` pick about 60% of the time
  so it isn't always the same. All tpl ids come from the DB, nothing gets guessed.
- Optional: stronger bot difficulty (aim and reaction) through APBS/SAIN when `Archetype == "Cheater"`.

## Profile page
The odd stats speak for themselves. Show a "BANNED" stamp plus the ban date once `BannedUtc`
is set. Hide nothing beforehand (no "cheater" label).

## Reporting
- The death screen already binds EFT's native `ReportPanel` (`UI/KillerProfileMenu.cs`), and
  PBT forces it offline. Re-enable the native "Cheating" row and point its click at a new route,
  `/pbt/killer/report-cheat` (account id + raid id).
- The web profile also gets a Report button, shown only for careers you have an `Encounter` with.
- When a report comes in, the server stores the `CheaterReport`, with `LostGear` snapshotted from
  the player's death inventory (equipment that wasn't in the secure container).
  One report per career per raid.

## Resolution (simulation tick)
- Once `DueUtc` passes: a real cheater rolls `BanChance`, and on a ban the career gets
  `BannedUtc` and `Retired`. It stops spawning and the profile keeps the stamp.
- The reward arrives as mail from a "Battlestate Support"-style system sender: "Thank you for your
  report. Action has been taken against a player you reported." It carries the `LostGear` items
  (insurance-style attachment) and/or roubles.
- Reports against a non-cheater resolve quietly ("no violations found"). Spamming them triggers
  the cooldown.
- Bonus: a cheater can trash-talk in DMs through the existing `BotMessages` tone system, with a
  new "cheater" tone (wipes the floor with you, then gets smug in chat).

## Things to check before coding
- The native `ReportPanel` row and callback for "Cheating" (decompile it, don't guess).
- The SPT 4.1 mail-with-items API (`MailSendService`) for the reward mail.
- Where the death inventory is available server-side at `/client/match/local/end`.
- Fika: reports and rewards stay per session.
