# Mod Actor Spawn Randomizer

> **Early development version: read-only.** The patcher currently analyses
> placements and *reports* what it would do. It does **not** disable anything
> yet, and the generated plugin contains no records.

Mod Actor Spawn Randomizer is a [Synthesis](https://github.com/Mutagen-Modding/Synthesis)
patcher for Skyrim Special Edition. Its goal is to thin out the enemies that
creature mods place in the world, reproducibly and before a playthrough starts.
For example, you could keep 30% of one creature mod's placements and 60% of
another's.

## How it works

1. **You choose the plugins.** Nothing is detected or included automatically.
   Each selected plugin has its own probability.
2. **Discovery.** For each selected plugin, the patcher finds the placed NPCs
   (`ACHR`) that **this plugin creates**. Overrides of other plugins' placements,
   including vanilla ones, are never candidates. Every fact is read from the
   winning record in your load order.
3. **Safety classification.** Each placement gets a status:
   - **Eligible:** selectable on its own.
   - **Grouped:** selectable, but only together with the other members of its
     encounter group. Members of a group share an activation trigger or other
     link target, or stand at exactly the same position.
   - **Needs investigation:** a structure that has not been validated yet. It
     is left untouched.
   - **Excluded:** a safety rule applies. It is left untouched.

   Anything the patcher does not positively recognise as safe is never
   eligible.
4. **Deterministic selection.** Each eligible unit is kept or rejected using a
   SHA-256 hash of the seed, the plugin filename and the local FormID:
   - The same seed, settings and plugins always give the same result,
     regardless of record order or unrelated plugins.
   - Changing the seed rerolls the selection. It never changes which
     placements are eligible.
   - The probability is an independent chance per unit, not an exact count.
     Small plugins vary more between seeds.

## Settings

| Setting | Meaning |
|---|---|
| `Seed` | Global seed (any 32-bit integer). Default `38174`. |
| `DetailedReport` | Also list every analysed placement with its status, decision and hash score. |
| `Plugins` | One entry per plugin: `Plugin` (picked from your load order) and `Probability` (0–100, the percentage of eligible units to keep). |

Each plugin's probability is applied independently. The same local FormID in
two different plugins produces independent decisions.

## The dry-run report

Each run prints a report to the Synthesis log. For every selected plugin it
shows:

- **Layout:** `Simple`, `Simple + links`, `Grouped` or
  `Probably not a creature mod`, plus any **warnings** that need manual review.
  The layout is a diagnostic only. A plugin with warnings can still have many
  eligible placements.
- **Eligibility counts** (from the safety rules, independent of the seed) and
  **simulated selection counts.** *REJECTED* means "would be disabled once the
  apply phase exists".
- **Reasons** for every placement that is excluded or needs investigation.
- **Encounter groups**, with their enable parent and shared link targets.
  Groups that span several cells are marked as location switches.
- **The in-game FormID** for every placement (for example `FE04DAFB`) next to
  its load-order-independent FormKey (`000AFB:Hand Placed Enemies.esp`), so
  you can look records up directly in xEdit or the console.

## Current limitations and safety restrictions

- **No apply phase.** Nothing is disabled yet.
- **Placements with an enable parent are not touched.** It has not been
  verified in game whether disabling such a placement works reliably. These
  are typically trigger-spawned ambushes and location-controlled groups such as
  Civil War forts, and they are reported as *Needs investigation*.
- **Excluded:**
  - placements referenced by other records (quest aliases, scripts, links)
  - quest-owned or death-counter reference scripts
  - the `Boss` location reference type
  - unique or essential bases
  - "opposite of parent" enable parents
- **Needs investigation:**
  - persistent placements
  - unknown reference scripts
  - location reference types other than `Boss`
  - merchant containers
  - persistent locations
- Intended as a pre-playthrough tool. It is not a runtime spawn manager.

## Installing in Synthesis

**From GitHub:** in Synthesis, add a Git Repository patcher with this URL:

```
https://github.com/hoskonen/synthesis-mod-actor-spawn-randomizer
```

and select the project `ModActorSpawnRandomizer/ModActorSpawnRandomizer.csproj`.
Alternatively, open [`ModActorSpawnRandomizer.synth`](ModActorSpawnRandomizer.synth)
with Synthesis.

**Mod Organizer 2 users:** Synthesis refuses to build patchers while it is
running inside MO2's virtual file system ("Build blocked: MO2's virtual file
system (VFS) is incompatible with .NET SDK builds"). Start Synthesis once
*outside* MO2 to let it build the patcher, then run it from MO2 as usual.

Requirements: Skyrim Special Edition, Synthesis, and the .NET SDK version
Synthesis asks for (the project targets `net10.0`).

## Development

```
dotnet build -c Release
dotnet test
```

The real-plugin validation tests run only when these environment variables
point at a local installation; otherwise they report as inconclusive:

| Variable | Required | Points at |
|---|---|---|
| `MASR_GAME_DATA` | yes | the Skyrim SE `Data` folder |
| `MASR_MODS_DIR` | yes | a folder containing the test plugins, such as an MO2 `mods` folder |
| `MASR_MO2_PROFILE` | optional | an MO2 profile folder, used to check in-game FormIDs |
| `MASR_REPORT_OUT` | optional | a file to write the detailed report to |

The selection algorithm (`ModActorSpawnRandomizer/v1`) reuses the hash
construction of
[Specters of Skyrim Randomizer](https://github.com/hoskonen/synthesis-spectersofskyrimrandomizer).
Golden-vector tests freeze it. Changing the algorithm requires a new version.
