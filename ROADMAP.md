# PBT roadmap

## Map preferences that change

Today every career's map taste is fixed. `MapWeight` (`Store/World.cs`) uses a stable
per-account roll, archetype biases, streamer maps and map names (FactoryRat). It never
changes, so a career's favourite map on day one is its favourite forever.

**Idea:** taste follows experience. Maps where a career does well pull it back, and maps
where it keeps dying push it away. Over time you start recognising the regulars on each map.

### How it works
- New `Career.MapAffinity: Dictionary<string, double>`, starting at 0 per map.
- After every raid (simulated, observed or story), nudge the affinity for that map:
  - survived: +0.08, plus a bit more for kills
  - died: -0.05
  - died to the player: -0.1 (they learn to avoid you, or see the rivalry idea for the opposite)
- Affinities slowly decay towards 0 (about 2% per simulated day), so tastes drift instead of locking in.
- `MapWeight` multiplies by `exp(affinity)` with the result clamped to 0.4-2.5. The existing stable roll
  stays as the baseline personality.

### Phases of a career
- **New player:** wanders, tries every map (affinity impact halved below level 10).
- **Settled:** one or two "main" maps take most raids.
- **Burnout:** after a long run on one map, a small chance to reset that map's affinity and roam
  again ("got bored of Customs").

### Where you see it
- Profile page: "Main map" plus a small top-3 map list with raid share.
- World log, occasionally: "Abramov has been living on Lighthouse lately".
- Bot DMs can mention it: "always you on Shoreline...".
- Matching/map indicators already count players per map, so the population per map shifts naturally.

### Settings (`simulation.json`, `World` block)
- `MapPreferenceDrift` (bool, default on)
- `MapAffinityGain`, `MapAffinityLoss`, `MapAffinityDecayPerDay`
- `MapAffinityMaxBoost` (2.5)

### Notes
- Raid outcomes already land in `AddRaids` and `AfterRaid`. Both need the map, and simulated raids
  pick one through `PickMap`, so it can be passed through.
- Name-based maps (FactoryRat) should start with a positive affinity on their map, not a fixed boost
  only, so even they can move on.
- Old saves: a missing dictionary means all zeros, so no schema bump is needed.
