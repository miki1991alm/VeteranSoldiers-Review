# Veteran Soldiers

**1.0.1 — portable veteran progression.** Individual experience and campaign progression for **Diplomacy is Not an Option**.

This release retains the completed content and fixes progression loss when switching computers. New saves carry their own veteran data, independent of the Windows account, game installation, profile path or local mod folder. Both computers need 1.0.1 or a compatible newer version. Checks and supported boundaries are described below. This document does not claim publication or universal compatibility. Historical observations retain their original version and scope.

Created and designed by **Kirapp**. This guide covers the manual Nexus package, whose `BepInEx` folder merges into the game folder. It does not describe the separate development installer.

## Requirements

- Windows x64, Steam game build `25478044`, displayed version `dno_1.0.187_r`.
- [BepInEx 5 with Unity ECS Support](https://www.nexusmods.com/diplomacyisnotanoption/mods/1), version **5.4.23.2-ecs**, Windows 64-bit. Ordinary BepInEx 5 is not a substitute. Download the dependency separately from its author.
- The supported `DNO.Main.dll` has SHA-256 `098B19C4580FF1C5CE4AA62D78E04BEF0429C2632EC4C36E4299270EE3CA3987`. The preloader rejects unsupported game code instead of attempting an approximate patch.

Other game versions, operating systems and untested mod combinations are not verified. A limited Resource Fairy check is described below; it is not blanket compatibility. An update from Steam can make this release incompatible. No DLC dependency has been identified for the tested tutorial scenario; other content has not been exhaustively tested.

The preloader makes rejected changes transactional: it prepares a copy and only commits a fully validated result, preserving the incoming assembly if it cannot complete. Health hooks declare their execution order and ignore a game method skipped by another hook. Controlled fixtures exercise these boundaries, but a later patch or a different health/save system can still conflict. These safeguards alone do not certify a third-party mod combination.

The mod provides English mod labels by default and an optional Spanish interface. The game's own text still follows the game's language settings. Existing custom rank names are preserved. The previously validated 0.2.0 interface used Spanish labels: Recluta (Recruit), Entrenado (Trained), Veterano (Veteran), Élite (Elite) and Legendario (Legendary).

## Manual installation

1. Close the game. Back up your saves and any existing `BepInEx/config/VeteranSoldiers/saves` folder. Keep your current mod configuration as well.
2. In Steam, open the game's **Manage → Browse local files** action. This is the folder containing the game executable, not its `Managed` subfolder.
3. Install the exact ECS-enabled BepInEx dependency using the instructions on its page. Start and close the game once to initialize it. If you already use a loader or other mods, check their requirements before replacing any loader files.
4. Extract this mod's ZIP somewhere convenient. Merge its `BepInEx` folder into the game folder. The two mod files must end up at:

```text
<game>/BepInEx/ecs_plugins/VeteranSoldiers.dll
<game>/BepInEx/patchers/VeteranSoldiers.Patcher.dll
```

5. Start the game. BepInEx creates `BepInEx/config/com.veteransoldiers.mod.cfg` if it does not already exist. Select a soldier to see the veterancy information.

Do not put the runtime DLL in `BepInEx/plugins`, duplicate it between plugin folders, or copy either DLL into the game's `Managed` directory. The supplied package contains no loader or game assemblies.

The `VeteranSoldiers` directory contains documentation, a reference configuration, dependency information, a content manifest, and the optional `Transfer-VeteranSave.ps1` tool with its `SAVE-TRANSFER.md` guide. The ten-entry package includes only two mod DLLs; the transfer script does not run during installation. The file `com.veteransoldiers.mod.cfg.example` is only a reference; it is deliberately outside `BepInEx/config` so an update cannot overwrite your settings. You can keep the documentation and tool outside the game folder.

Manual installation is the documented route. Mod-manager deployment has not been validated. If you previously installed a development/personal package using its managed installation scripts, use that package's matching upgrade or removal procedure instead of silently replacing files tracked by its manifest.

## What the mod does

Each eligible soldier has an individual XP record. Attributable effective hostile damage earns XP; standing near a fight does not. Units do not share XP merely because they are the same type. Death removes the soldier's active progression record.

The default progression is:

| Rank | Total XP | Damage bonus | Maximum-health bonus |
|---|---:|---:|---:|
| Recruit / Recluta | 0 | None | None |
| Trained / Entrenado | 100 | +5% | None |
| Veteran / Veterano | 300 | +10% | +10% |
| Elite / Élite | 700 | +15% | +20% |
| Legendary / Legendario | 1500 | +25% | +30% |

Increasing maximum health does not automatically heal current health. Vanilla research is preserved by the modifier design; every research/skill combination has not been certified in play.

Defaults award one XP per five points of effective damage. A kill bonus is awarded only when attribution is unambiguous; simultaneous or partly unattributed lethal damage can omit the bonus. Hostile structures use a 0.25 XP multiplier, and eligible siege units use a 0.5 multiplier. Enemies do not gain veterancy by default. These balance settings are configurable and are not certified for every troop type or long campaign.

Four routes are instrumented: normal melee, projectile impact, direct-control melee and the native doctor/allied-healer request path. The first three attribute damage; the fourth identifies healing for the recovery doctrine and never awards XP. These jobs run as managed main-thread work. Cocoon lightning and all possible special abilities are not covered; the 0.5 benchmarks do not measure the added healing route.

## Promotion notices

Friendly combat promotions produce a clickable card below the resource HUD. Promotions arriving close together are grouped by rank after a 0.6-second batching window; one card stays visible for five seconds. Holding the pointer over it pauses expiry for up to ten additional seconds without pausing the game. Click an individual notice to inspect and locate the surviving soldier, or a grouped notice to open the register. The close button dismisses the card. Restoring saved XP does not generate a combat-promotion notice.

Set `Interface.ShowPromotionNotifications=false` to disable the cards. Hiding the mod visuals, opening the journal, entering a recognized menu or changing session discards pending notices instead of replaying old promotions. Historical 0.4 review covered English and Spanish individual/group notices at 1080p/720p, along with a custom Ctrl+F7 shortcut. An insignia/card overlap found at 720p was corrected and visually rechecked in that version's final Spanish build. Those earlier observations do not validate the new clickable cards.

## Selection panel and insignias

The mod adds information inside the original selection panel. A single soldier shows rank, XP, progress and bonuses. A group shows its rank distribution and total XP while retaining the game's native selection and type filters. Damage figures follow the native panel's meaning; they are not a prediction of every hit against every target.

Five shared shield icons use different markings as well as gray, bronze, silver, gold and ivory. Above the battlefield, only selected soldiers above Recruit rank receive insignias. Favorites receive a gold corner tab; specialization ribbons use one, two or three strokes as well as color. No armor, clothing, weapon or troop model is replaced.

The renderer filters dead or fog-hidden units and off-screen positions. It avoids recognized HUD regions and limits density: at most 64 visible insignias from up to 256 selected candidates, prioritizing higher ranks. Large selections will not show a marker over every soldier. Selection refreshes at 5 Hz, so visibility or rank changes can take about 200 ms to appear. Geometric occlusion behind buildings and every possible third-party HUD are not certified.

## Veteran journal and army filters

Click the **Veterans** button beside the minimap, use the selection panel's journal access, or press **Ctrl+F9**. The button leaves the right-hand alerts clear and blocks battlefield input while hovered. The journal has **Army register**, **Hall & doctrines**, **Memorial** and **Settings** tabs. It uses the native canvas, font, click sounds, tooltips and confirmation window. Opening it preserves the game's existing pause state. Escape closes it; recognized menus, research, market, photo mode and direct control close or block it. Isolated 0.6 tests exercised nickname entry, battlefield-input blocking, focus release, and native specialization cancellation/confirmation. Those checks cover the tested interactions; they do not certify every key sequence or another mod's interface.

The register shows eligible living soldiers and army totals. Cycle the rank, unit-type and specialization filters, or enable Favorites. Eight rows appear per page. Clicking a row opens that soldier's dossier; **Select & locate** changes selection and camera position without ordering movement. Soldiers in towers or inside buildings remain in their posts. Register selection controls act on available soldiers from the filtered list; the selectable count can therefore be lower than the list count. The army snapshot refreshes at most once per second while visible.

For a smaller filter, click a rank card in the native **group-selection panel**: this keeps only that rank from the current selection. It does not select matching soldiers elsewhere. An empty rank filter preserves your current selection.

## Names, service history and decorations

The dossier shows current rank, XP, health, progress to the next rank, specialization and recorded combat contributions. Enter an optional name and click **Name** to apply it. Names contain up to 28 characters; control characters and rich-text markup are removed. Favorites help you find individual soldiers and add a battlefield marker. Save the game with persistence enabled to retain these choices, including for recruits who have earned no XP.

Histories retain the latest 12 significant events: first verified contribution, promotions, decorations, specialization and an observed death. They do not store every hit or invent a past career from an old XP total. Damage contribution and confirmed eliminations are separate statistics; ambiguous lethal damage cannot earn a confirmed-elimination decoration.

| Decoration | Requirement recorded since this feature was installed |
|---|---|
| Baptism of fire | 250 effective hostile damage contributed |
| Battle hardened | 2,000 effective hostile damage contributed |
| Slayer | 25 unambiguously credited eliminations |
| Champion | One confirmed elimination of a target classified as powerful: a siege unit or a unit with native power level at least 5 |
| Siege breaker | One confirmed hostile-building elimination through the enabled structure-credit path |
| Veteran's honor | An observed promotion to Veteran rank or higher |

Decorations are recognition, not additional stat bonuses. The memorial retains the latest 256 recorded fallen soldiers, while its campaign death total can be larger. A death requires an observed positive-health-to-zero transition; dismissal, disappearance, entity recycling and a world reset do not create memorial entries. The memorial remains accessible after a Hall is destroyed. History belongs to the saved campaign and does not transfer soldiers between missions.

## Optional specializations

At **Veteran rank or higher** (300 XP with default thresholds), an individual friendly soldier may choose one specialization from the dossier. Choose a path and approve the native confirmation dialog for that soldier. The target is rechecked when confirmed. The choice is optional and permanent; there is no respec button. With `General.Persistence=true`, save your game to preserve it. With persistence disabled, choices last only for the current session. A Hall is not required for XP or personal specialization.

| Specialization | Personal effect |
|---|---|
| Assault | Adds 5 percentage points to the rank's damage bonus. |
| Guardian | Adds 10 percentage points to the rank's maximum-health bonus, without healing current health. |
| Mentor | Multiplies newly earned XP by 1.2. It does not multiply stored XP or another soldier's rewards. |

For example, the default Elite rank with Assault has a combined +20% damage bonus; Elite with Guardian has +30% maximum health. Effects remain subject to the configured safety caps. They do not edit a shared troop prefab or grant the same choice to every unit of that type.

`Specializations.Enabled` defaults to `true`. Set it to `false` with the game closed to suspend these effects while preserving saved choices. Re-enabling restores eligibility for those effects; it does not grant free healing. A retained choice is also inactive below Veteran rank if your rank settings or maximum level change.

## Hall of Veterans and doctrines

The Hall is a University replacement with an original low-poly visual model. The ordinary University and Hall share a limit of one building, including one under construction. It retains the native University's footprint, price for new construction, workers, research role, health and repair contract. The **Build a hall** button opens its native construction category. Availability follows the mission's University catalog; it does not add human buildings to undead missions or bypass campaign restrictions.

If you already own a completed ordinary University, use the Hall tab's dedication action to convert it for free. Dedication keeps the same building entity, researchers, health and native orders; it does not build a second research producer. Existing native University research completion and free-building entitlements are retained by the implementation. Runtime coverage is limited to the recorded checks below; every dedication, research and mission combination has not been certified.

If a perk grants a free ordinary University, build that University and then dedicate it; direct Hall construction does not spend a University-specific free-building entitlement. The research integration copies effects that actually target the University's discovered object ID into the Hall's native research view, preserving the ordinary University's effects and any explicit Hall effects supplied by another mod. It adds no unrelated research benefits and does not change the research assets.

The Hall tab provides access to living legends, the memorial and doctrines. A completed, surviving Hall is required for doctrine effects and preparation. Destroying it suspends effects and preparation; rebuilding resumes them without awarding progress for the interruption. The journal, individual progression and memorial remain available without a Hall.

**New construction requires Town Hall III on the supported game build.** The Hall tab shows your actual Town Hall level if it is too low. Completing a Town Hall or another building does not itself create a Hall: build the dedicated Hall, or use the free dedication action for a completed ordinary University.

Each doctrine card shows your campaign combat XP, its milestone progress, resource price and current blocking reason. **Adopt doctrine** appears when its requirements are met. **View requirements** explains a missing Hall, dedication, XP, resources or an existing preparation without charging resources or opening an adoption confirmation. If the Hall is finished but adoption is unavailable, check that reason first; constructing the building does not bypass XP or resource requirements.

| Doctrine | Campaign milestone | Adoption cost | Effect |
|---|---:|---|---|
| Military tradition | 100 verified combat XP | 100 wood, 50 stone | +10% newly earned combat XP |
| Replacement corps | 300 verified combat XP | 150 wood, 50 iron | 15% less human troop training time, composed with existing research |
| Veteran recovery | 600 verified combat XP | 100 food, 50 iron | +15% attributed doctor/allied-healer healing received by Veteran rank or higher |

Milestones count new, eligible player combat XP before specialization/doctrine multipliers. They do not retrospectively count stored XP, grant proximity XP or progress from enemy veterancy. Each adoption takes 60 simulation seconds. Payment uses native resources and is checked again on confirmation. Only one doctrine is active; an existing doctrine remains active while its replacement is prepared. The Hall has its own saved preparation queue and does not occupy the game's research queue. Recovery grants no passive healing, does not heal when toggled and awards no healing XP. Training changes do not discount recruitment prices. Cancellation uses the native refund formula and the unit's actual remaining training progress, which advances faster under Replacement corps.

Geometry and icons are generated by the mod. Players need neither Unity Editor nor Blender, and the distribution does not contain the game's building models or other proprietary assets.

## Local conflict report

Press **Ctrl+F10** during gameplay or click **Create diagnostic report** on the journal's Settings tab. The mod writes a text snapshot to `BepInEx/config/VeteranSoldiers/diagnostics` and displays its filename or a short error code. Reports are generated only on request, with a five-second cooldown. Nothing is uploaded automatically.

The report lists bounded installed-DLL metadata, relevant loaded assemblies, plugin identifiers and visible Harmony patches on watched methods, including declared priorities and ordering relationships. It flags duplicate assemblies and potential hook overlaps. It omits filesystem paths, machine/user names, configuration values, saves and exception messages; suspicious metadata is redacted. Review a report before sharing it.

**A warning is a lead for investigation, not proof of a conflict. No warning is not a compatibility guarantee.** Separate Harmony libraries, native detours, previous preloader changes and mods that write statistics directly may be invisible. A partial inspection is labeled as such. The report does not fix conflicts or decide which mod to remove.

| Control | Effect |
|---|---|
| F8 by default | Temporarily hides/restores mod information and insignias; closes the roster and discards promotion cards. XP continues. |
| `Interface.ToggleInterfaceShortcut` | Defaults to `F8`; use another shortcut, such as `F8 + LeftControl`, or `None` to disable the toggle. |
| Ctrl+F9 / `Interface.ArmyShortcut` | Opens/closes the veteran journal. Default config syntax: `F9 + LeftControl`. |
| Ctrl+F10 / `Interface.DiagnosticsShortcut` | Creates a local conflict report. Default config syntax: `F10 + LeftControl`. |
| `Interface.ShowPromotionNotifications` | Enables grouped notices for friendly combat promotions; defaults to `true`. |
| `Interface.ShowPanel` | Enables the veterancy strip inside the native panel. |
| `Interface.ShowVeteranInsignias` | Enables rank shields in the panel and over selected veterans. |
| `Interface.ShowExperienceBar` | Enables the individual XP progress bar. |
| `Interface.PanelScale` | Scales the veterancy strip from 0.75 to 1.5; does not scale the whole HUD or battlefield icons. |
| Settings → **Language: English** / **Idioma: Español** | Switches the mod's interface immediately and saves `Interface.Language`. Does not change the game's language or custom rank names. Restart only when editing the configuration file manually. |

Turning off only `ShowPanel` can leave battlefield insignias enabled. Turning off both visual options skips the shared selection presentation work.

The shortcut is ignored while a recognized text field has focus. It does not consume or reserve the key: another mod or the game can still respond to the same shortcut. Choose a different binding if they overlap. Close the game before editing these settings and restart afterward.

## Settings

The journal's Settings tab applies and saves the selection panel, XP bar, insignias, promotion notices, panel scale and English/Spanish mod language immediately. The language button retains your current soldier, filters, pages and unsaved name draft. For other settings, close the game before editing `BepInEx/config/com.veteransoldiers.mod.cfg`, then restart. Common options include:

- `General.Enabled`: enable the system. Set to `false` before launch to skip the preloader's instrumentation; changing it mid-session is not a way to restore vanilla scheduling.
- `General.Persistence`: save and restore verified progression records.
- `General.DebugLogging`: keep `false` for ordinary play and performance measurements.
- `Specializations.Enabled`: enables optional personal choices at Veteran rank; disabling preserves saved choices but suspends their effects.
- `Experience.ExperienceMultiplier`, `DamagePerExperience` and `KillExperience`: control progression speed.
- `Units.EnableSiegeUnits` and `Units.EnableEnemyVeterancy`: eligibility options.
- `Rank1` through `Rank5`: XP thresholds and bonuses. Values such as `0.10` mean 10%. Recruit must retain zero XP and zero bonuses; thresholds must increase strictly. An empty `Name` uses the selected interface language; an explicit custom name is retained.

The shipped example lists the supported settings. Invalid rank settings or unsupported unit-type names can prevent initialization; restore a known valid configuration rather than guessing names.

## Saves, updates and removal

The journal reports the actual save state: session progress, an outstanding save, the last completed verified save, a restored snapshot, a failed save, or rejected mod data. A completed save describes that snapshot; save again after later changes. If saving fails or mod data is rejected, retain an earlier matching save/sidecar backup and check the local log.

With `General.Persistence=false`, the game still saves normally, with the mod's removable health/building normalization, but no portable snapshot or new mod sidecar is written and an existing sidecar is not replaced. Progress earned in that session is not retained. Overwriting a native slot changes its fingerprint, so an older XML left beside it is not a substitute for a matching backup.

Starting with 1.0.1, progression is stored in a bounded, checksummed trailer inside the extensionless native save file. The game's original serialized bytes stay unchanged. The mod verifies those bytes, the complete progress document and the exact saved-unit references before restoring anything; it never guesses identity by unit position or type. A copy to another Windows path or filename keeps that binding intact. Steam Auto-Cloud can carry the data with the native save it already synchronizes; the mod does not change Cloud settings or upload separate files.

The mod also maintains a local compatibility backup in `BepInEx/config/VeteranSoldiers/saves/*.xml`. Portable data takes priority over any stale XML on the receiving computer. Corrupt portable data is rejected rather than silently replaced by an unrelated local backup. A missing local XML does not prevent a verified portable load. A failed local backup is reported in the log; it does not invalidate an already completed portable save.

Version 1.0 retains sidecar schema 3: individual progression and specialization, names, favorites, bounded histories, decorations, memorial, campaign counters, exact Hall references and doctrine state. Schemas 1 and 2 remain readable; missing historical information starts empty and is not reconstructed from XP. Older mod versions cannot be assumed to read schema 3; preserve pre-update saves and sidecars for rollback.

The Hall save design writes native University IDs into the game's serialized object and records Hall identity and doctrines in the portable snapshot, with the local sidecar as a compatibility backup. The supported native reader ignores the trailer when the mod is absent. Saving again without the mod discards its progression; retain the original portable slot before doing that. Historical 0.6/1.0 tests restored unfinished Halls, pending preparation and the removable University fallback. Those tests retain their original binary scope. Loading an earlier slot restores its earlier campaign history and does not merge later deaths or doctrine progress.

The loader also validates the current world's research effect contract before restoring Hall identities. An unsupported contract leaves native Universities in place and skips Hall/doctrine restoration for that load. Keep the original save and sidecar before saving this fallback state. If another hook skips native research initialization, Hall doctrine effects are suspended because their integration has not been established.

Repeated XP or snapshot restoration preserves the health factor already owned by a soldier. Interrupted cleanup remembers factors already removed, avoiding a second division on retry. Invalid snapshots and disabled-runtime restores are rejected before changing health. Sidecar verification precedes the game's load-complete notification. Automated tests cover the health methods with an in-memory ECS boundary. Historical isolated Unity sessions verified repeated restoration, a reversible simulated multiplier and exact-identity save/load in earlier versions; this does not certify the complete 0.6 system, every campaign or another mod.

Save preparation validates a complete copy before publishing its changes. If XP or specialization is invalid but health is safe, that soldier's progression is omitted from the sidecar and its recorded veteran health factor is still removed from the base save. If health or normalization is unsafe, the mod abandons the entire prepared batch: the base game continues saving its originally captured statistics and no new sidecar is written. Those captured statistics may still contain a veteran bonus. This prevents partial normalization but cannot guarantee a clean or recoverable save after arbitrary data corruption. Check the log and retain a known-good backup rather than assuming every save has a matching sidecar.

Hall-ID normalization is a separate save boundary. If it fails, the mod raises that failure instead of allowing an unnormalized custom building ID through the collection step. Such an error is not a successful save and must be investigated before relying on that slot.

Back up **both your game saves and this sidecar folder**, especially when migrating older slots. Steam Cloud does not synchronize the old installation-local sidecars. Do not edit sidecars to force a match. A hard shutdown during the append may leave an incomplete trailer; retain an earlier verified slot. The native serialized prefix is never rewritten by the portable writer. Other mods that replace serialization or append their own trailers require a joint compatibility test.

### Transferring to another folder or computer

For 1.0.1 saves, keep the same compatible mod installed on both computers, save normally, wait for the journal to confirm the portable save, exit normally and let Steam finish synchronizing before opening the other PC. When copying manually, copy the extensionless save and its `.dat` header together. No XML path rebinding is needed for the portable snapshot.

**Recovering progress from 1.0.0 or earlier:** update the computer where the levels are still correct first. Keep its native save and local sidecar together, load that exact slot, check the levels and save under a new name. That save now includes the progress. Exit and let Steam synchronize it; then load it on the other computer with 1.0.1. Updating only the receiving PC cannot recover XP that never reached that PC. Do not overwrite the sole source copy. If the original slot was already overwritten on both computers, a matching native save/sidecar backup is required; the mod cannot reconstruct missing XP.

The included [SAVE-TRANSFER.md](SAVE-TRANSFER.md) and `Transfer-VeteranSave.ps1` remain available for legacy saves and complete private backups. They require a matching local sidecar and Windows PowerShell 5.1 or PowerShell 7. Keep both games closed; inspect and preview imports before applying them under a new save name.

Import preserves the game/header bytes and rebinds the complete sidecar to the new exact save path, including schema-3 histories, memorial, Hall references and doctrines. It refuses existing destination files and validates archive entries, sizes, hashes and XML. Ordinary I/O errors roll back files created by that invocation where safe; power loss is not an atomic three-file transaction. The tool does not convert game versions, authenticate untrusted saves, synchronize Steam Cloud or transfer another mod's data. Use your own or trusted saves and keep backups. Do not upload private transfer bundles as mod files or public bug reports.

For a manual update, close the game and back up the previous two DLLs. Replace only those mod DLLs from the new package; preserve configuration and sidecars. Check the new release's game compatibility first.

For manual removal, close the game and remove only:

```text
BepInEx/ecs_plugins/VeteranSoldiers.dll
BepInEx/patchers/VeteranSoldiers.Patcher.dll
```

Keep the loader, other mods, configuration and sidecars. Do not delete the entire `BepInEx` folder. If you used the development installer, use its matching uninstaller instead.

A 0.6 Hall save was manually loaded in the isolated copy with both mod DLLs absent and BepInEx retained: the building appeared as a native University with 1300/1300 health and 5/5 researchers. No new save or full campaign was tested in that state. A historical `Persistence=false` reload restored vanilla soldier health without XP. Retain backups before disabling, updating or removing the mod.

## 1.0.0 validation

The public and QA builds each pass **2827 code/fixture assertions**, with no compiler warnings or errors. These cover calculations, persistence/health, the supported game's IL contracts, actual Harmony ordering fixtures, diagnostics, Hall rules, chronicle lifecycle and research aliases. Instruction-level assertions are not independent gameplay scenarios. The public DLLs exclude all development harnesses.

The isolated-game core run contains **40 successful checks and one explicitly inconclusive index-recycling observation**, followed by **eight successful checks in a new process**. An actual projectile earns XP for its attacker while an idle Archer retains zero. Later damage/perk stimuli are explicitly synthetic. The saved soldier retains its exact current-save reference, 931 XP, Guardian, 25.875 health, 1.3 health factor, name, favorite and contribution counters. Loading the earlier snapshot discards its later memorial entry. Index recycling was observed in a separate 0.6.1 baseline run; it was not observed in the final 1.0 core run.

Additional isolated native-game observations cover:

- Saving with persistence disabled both over an existing test slot and into a new slot: the old XML remains unchanged and no new XML is created. Re-enabling persistence writes the same soldier career using the current capture's reference map.
- Loading without a sidecar, saving the native University fallback and loading it again with the mod installed. Malformed XML is rejected visibly while the campaign remains usable with a native University, zero restored XP and no restored doctrine.
- Restoring a Hall through a nonempty exact-reference match. Sampled Replacement-corps ticks apply the 15% reduction and restore the original training map. Native pending training remains present after a reload and continues; this does not independently validate citizen identity across captures or every queue/research combination.
- Cancelling an identified native Archer training entry removes that entry. The observed partial refund was two wood, one food and one citizen; refund rules remain the game's responsibility and are not promised to be a full refund.
- Adopting Recovery through the journal's button and native confirmation charges 100 food and 50 iron, retains Replacements during preparation, then activates Recovery under the native clock. Attributed friendly Doctor requests change from 10 to 11.5 healing for the tested Legendary soldier and respect the maximum-health cap. The doctor and wounds were prepared for the test; other healing can also contribute to a frame's net health change.
- Clicking a promotion notice after its exact target becomes unavailable opens the army register. This test removes a fixture entity; it is not a natural combat death or evidence of a new memorial entry.
- An actual controlled filesystem write failure leaves 36 previous sidecars intact and shows the failure in the journal. Removing the test blocker permits a later verified save. The failure message was reviewed in English at 4K and Spanish at 720p, alongside live language switching and the diagnostic button.

Nineteen acceptance assertions check the archived native observations above. These scenarios supplement the earlier construction, repair/research, doctrine and UI evidence below. They are bounded tests on the supported Steam build, not a guarantee for every campaign duration, hardware configuration or third-party mod.

## Validation recorded before 1.0.0

The following section preserves the earlier beta checks and their original limits. Current release checks are recorded above.

### Historical 0.6.1 validation

The 0.6.1-beta QA and public builds each passed **2827 code/fixture assertions**, with zero compiler warnings or errors. This includes 24 HUD-layout and 25 additional doctrine-eligibility regression checks. These are assertions, not independent gameplay scenarios. The public build excludes development harnesses.

An isolated copy of an existing campaign reproduced native right-hand alerts and a Town Hall II restriction; the corrected access button left those alerts clear and opening/closing the journal preserved the selected Town Hall. Doctrine cards displayed the actual Town Hall III requirement and current campaign XP. In a separate controlled save with an operational Hall, actual native confirmation/cancellation was exercised: cancelling preserved resources; confirming Tradition charged exactly 100 wood and 50 stone, kept the previous doctrine active during preparation and later activated Tradition. A blocked second adoption left resources and the pending doctrine unchanged. Recovery confirmation charged 100 food and 50 iron. The prepared fixtures and accelerated native clock do not establish organic campaign balance. Remaining 0.6 content/performance evidence below retains its original binary scope.

The final QA build restored 22 soldiers, the exact Hall reference, active Tradition and pending Recovery in a fresh process. Preparation remained unchanged while paused; the visible explanation and cards were inspected in Spanish at 720p and English at 1080p, with live mod-language switching. The right-hand-alert scene was inspected at 4K in the preceding candidate. These bounded checks do not certify every screen or mod combination.

### Historical 0.6.0 validation

The final 0.6.0-beta QA and public builds each passed **2778 code/fixture assertions**, with zero compiler warnings or errors. These cover progression, persistence, patcher contracts, Harmony boundaries, diagnostics, Hall rules, chronicle lifecycle and research aliases; they are not 2778 independent gameplay scenarios. The public build excludes the development harnesses.

The tested QA runtime passed **35 checks plus one explicitly inconclusive entity-index-reuse observation**, followed by **eight fresh-process persistence checks**. The tests distinguished actual projectile XP from later synthetic requests. The same saved soldier reference returned with 931 XP, Guardian, 25.875 health, its 1.3 health factor, name, favorite and recorded contributions. Loading an earlier snapshot also discarded its later memorial entry. Actual ECS index recycling was not observed.

Separate content sessions produced **19 bounded acceptance checks**:

- Native Hall construction and exact-reference save/load were exercised, including an unfinished Hall and a completed Hall with pending Recovery. Native construction work and doctrine preparation continued after fresh-process loading; exact construction duration was not measured.
- Native research 10 completed through the game's research system and raised maximum health from **1000 to 1300** on the University prefab, Hall prefab and the same living Hall. Its exact resource payment was not independently isolated by archived snapshots.
- Recovery changed an attributable friendly Doctor request from **10 to 11.5 healing** for an eligible Veteran. The doctor and wound were prepared for the test. Health increased below the cap; the net observed change was slightly larger than 11.5 and cannot be attributed entirely to that one request.
- Four sampled Replacement-corps training ticks advanced by **simulation delta / 0.85**, with the native research map restored after each tick; four baseline ticks were also checked. A later run with the idle-training guard still emitted the native training command and showed the same progression in four sampled ticks. This verifies sampled queue progress, not complete recruitment duration or final recruit identity.
- A native training-cancellation command was emitted. Across the captured cancellation pair, resources increased by **3 wood, 1 food and 1 unemployed citizen**, with stone and iron unchanged. These aggregate changes do not establish the removed unit type, the final queue contents or every cancellation/refund scenario.
- Native doctrine confirmation produced the expected **150 wood and 50 iron** decrease for Replacements while Recovery remained active. Preparation stopped across the captured Hall-loss states and advanced again after dedication. Free dedication kept the same University entity and recorded resources; that pair does not independently prove researcher continuity or exact before/after health preservation.

English and Spanish UI checks covered 720p and 1080p, live language switching, actual naming, input/focus handling and specialization cancellation/confirmation. The corrected doctrine cards retain one XP requirement, all costs and the 60-second preparation time. Individual and grouped promotion clicks were repeated successfully in the final QA build at 720p; the unchanged layout was also reviewed at 1080p in the preceding candidate. An unintended battlefield click when dismissing a notice was corrected: the final QA check preserved the exact selected soldier in the game state, selection tags and native panel, left the journal closed and released gameplay input. These content and visual checks span successive development builds with prepared resources, units and controlled simulation speed; they do not establish organic campaign balance or repeat every interaction on the final DLL.

A real schema-3 save was exported, inspected and imported into a new slot on the same PC, with **22 soldier records** and no existing files overwritten. Loading that imported slot restored all 22 records, and this load was repeated successfully in the final QA build. Exact Hall reference **17879** matched the one expected custom Hall, operational with Replacements active and **1300 maximum health**. This verifies a real transfer and subsequent game load on this machine; it is not a second-PC, Steam Cloud or different-game-version test.

A separate manual load with both mod DLLs absent verified the native University fallback, including its model, panel, research-enhanced 1300/1300 health and 5/5 researchers. It did not test resaving or extended play without the mod.

Runtime interaction with a promotion notice whose target has become unavailable was not exercised. Long campaigns, all troop/research combinations, storage failures, ultrawide presentation and untested mod combinations remain outside the verified scope. The 0.6.1 review adds the specific 4K scenes described above; it is not an exhaustive 4K interface certification.

## 1.0.0 performance observations

Two matched pairs of prepared 1000-unit battles at 3840×2160 averaged **84.95 FPS enabled versus 104.44 FPS disabled** on this test PC (+22.93% mean frame time; +26.04% p95). Enabled samples were 85.08/84.83 FPS; disabled samples were 105.11/103.77 FPS. All four retained focus and shared the same QA runtime, game code, normalized configuration, layout, seed, quality and frame-limit settings. An earlier sample with seven unfocused frames was excluded.

Each run used 15 seconds of warmup and 30 seconds measured. Combatant starting health was scaled by 100 and one Town Hall by 10000 to sustain the fight; no damage, healing or XP was injected during measurement. Fighting-unit counts still differed slightly. These are QA-harness frame timings, including rendering and operating-system work, rather than direct timings of the public DLL or isolated mod CPU cost. They do not measure an operating Hall, an open journal, long campaigns or every PC. The managed instrumentation has a meaningful cost; this release does not claim a performance optimization.

## Historical 0.6 exploratory performance

One prepared 0.6 pair with 1000 combatants recorded **83.4 FPS enabled versus 103.3 FPS disabled** (+23.88% mean frame time; +25.46% p95). Both used the same QA runtime, normalized configuration except Enabled, seed and layout, at actual 3840×2160 on an i7-13700KF/RTX 4080, with 15 seconds of warmup and 30 seconds measured. Both retained focus. Initial troop health was multiplied by 100 and one Town Hall by 10000 to sustain the scenario; no damage, healing or XP was injected during measurement.

The battles differed: average fighting-unit counts were 609.1 and 587.2. This is one pair in a fixed order, with no statistical repetition or complete control of background work, temperature and clocks. Frame intervals include rendering, job waits and operating-system scheduling. The timed QA binary predates the final UI/validation changes; it is **not a measurement of the final public build**, an isolated medical-hook cost, a 0.5-to-0.6 comparison or a test of an open journal and operating Hall. Three damage jobs and one healing job run as managed code; these results do not support a negligible-cost claim.

## Historical compatibility and performance evidence

Earlier 0.4 checks covered bounded combat/save cases with **Resource Fairy (download 0.0.7, internal version 0.0.6)**; 0.5 added a limited transfer/restart check with that plugin present. Resource Fairy's economic effect and the plugin alone were not measured, and those sessions do not certify its combination with 0.6. Trainer, Legendary Research and other health/damage/save mods remain unverified. Historical Player logs also contained video/advertising errors of unconfirmed cause, so those complete logs are not described as error-free.

Historical 0.1/0.2 timings were exploratory single pairs. A later six-run experiment compared two patchers using the same 0.5 QA runtime, not complete mod releases; its small differences overlapped and did not demonstrate a general FPS improvement. None of those earlier results substitutes for the current 0.6 evidence or establishes universal campaign performance.

## FAQ and support

**Why does my new soldier have no battlefield icon?** Recruit icons appear in the panel, but battlefield insignias begin at Trained. Select the soldier and check F8 and the visual settings. Density limits can also hide icons in crowded selections.

**Why did a kill give no bonus?** The mod only credits an elimination when the lethal damage can be attributed without ambiguity. Damage XP can still be awarded separately.

**Why did XP not return after moving a save?** Sidecars are tied to the original save path and exact save bytes. Use the included transfer tool on the original matching set; copying a slot alone is not a progression transfer.

**Can I change a specialization?** Each eligible soldier gets one optional choice. It cannot be reassigned through the interface. Disabling specializations suspends effects and retains the choice.

**Why is the Hall unavailable?** The mission must expose the native University and support the human construction catalog. An existing University or Hall uses the shared building slot; finish an ordinary University and dedicate it through the Hall tab. A catalog-registration error also disables Hall access; check the local diagnostic information and log. The mod does not unlock restricted campaign buildings.

**Why can't I adopt a doctrine after building?** Finish the actual Hall, or dedicate a completed ordinary University for free. The shared native building requires Town Hall III. Each card shows campaign combat XP against its 100/300/600 milestone, the missing requirement, resources and preparation time. Use View requirements for the full explanation. A doctrine already active or preparing cannot be ordered again. Preparation takes 60 simulation seconds with an operational Hall; resume the game if the panel says Paused. The old doctrine stays active until the replacement finishes.

**Why does an old veteran have no medals or service history?** Existing XP is retained, but earlier battles were not recorded by this feature. History and campaign milestones begin with new verified events; old careers are not invented.

**The panel or mod does not appear.** Check the supported game build, the exact ECS loader, both DLL paths, `General.Enabled`, visual settings and F8. Read `BepInEx/LogOutput.log` for the first relevant initialization error. Do not bypass a compatibility rejection by replacing the game's DLLs.

**Can I use this with another health, damage or save mod?** Resource Fairy was present for the limited 0.5 transfer/restart check; broader combined combat checks belong to historical 0.4 evidence. Its economic effect was not measured. Other health/damage/save combinations remain unverified. Use the local conflict report as supporting information, then test exact versions and settings on backed-up saves. Transactional patch rejection, hook-order tests and simulated multipliers reduce specific risks, but do not certify another mod.

When reporting a problem to Kirapp, include game version, mod version, loader version, other installed mods, reproduction steps and the relevant log excerpt. Remove personal paths or identifiers before posting. Do not upload personal saves publicly unless you deliberately choose to share them.

See `CHANGELOG.md` for the validated development history and `CREDITS_AND_PERMISSIONS.md` for authorship and AI disclosure.
