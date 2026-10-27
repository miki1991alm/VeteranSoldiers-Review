# Veteran Soldiers — changelog

Created and designed by Kirapp. Earlier entries describe local development releases; **0.3.0-beta** is the first public beta.

## 1.0.1 — progression follows the saved game

- Fixed loss of veteran levels when moving a saved game between computers. New saves include a bounded, checksummed progression snapshot inside the native save, independent of local Windows paths.
- Preserved exact saved-unit references, XP, health normalization, specializations, personal chronicles, memorial, Hall identity and doctrine state. Portable data takes priority over stale destination-side XML.
- Retained legacy sidecar reading and local compatibility backups. Load and re-save an old slot on the PC that still has the correct levels to migrate it. Missing historical XP cannot be invented or reconstructed from the vanilla save.
- Kept the native serialized bytes unchanged and retained persistence-disabled behavior. In-process append failures roll back the appended bytes; power loss and third-party save replacements remain limitations.
- Added cross-path, corruption, bounds and write-failure regression tests and an opt-in isolated fresh-process test that copies only save/header under a new name without a local sidecar. Steam synchronization between two physical PCs still needs user acceptance.

## 1.0.0 — completion of the existing feature set

- Kept the existing progression, specializations, journal, chronicle, Hall and doctrine content. This release adds no new gameplay systems.
- Fixed `Persistence=false` still scheduling an empty campaign sidecar after a native save. Disabled persistence now leaves existing sidecars untouched and creates no new sidecar, while keeping removable base-game health/building normalization.
- Replaced the journal's unconditional saved-progress message with actual session, saving, last verified save, restored, rejected-data and save-failure states. Saving again is still necessary to preserve later changes.
- Added an isolated runtime regression that overwrites a native test save with persistence disabled, checks the old XML is unchanged, makes a second save without creating XML, re-enables persistence and verifies the exact soldier career in the current save's reference map.
- Retained schema-3 storage, older-schema reading, configuration values and the supported Steam build. Back up matching native saves and sidecars together before updating.

The accompanying guide records the checked scenarios and remaining platform/mod boundaries. The release number does not imply compatibility with every game update or third-party mod, and this changelog does not assert publication.

## 0.6.1-beta — HUD placement and clear doctrine requirements

- Moved the Veterans access button beside the native minimap, following its screen bounds and leaving the right-hand alerts clear. Hovering the button now blocks battlefield interaction generation, preserving native selection when opening the journal.
- Replaced ambiguous disabled adoption buttons with visible requirement explanations. Cards show campaign combat XP, milestone progress, the actual blocker, costs and preparation time; only eligible actions say Adopt doctrine.
- Exposed the Hall's native Town Hall III construction requirement, including the current Town Hall level, and distinguished construction from free University dedication. Existing combat XP, unlocks, costs and schema-3 saves are retained.
- Centralized doctrine eligibility for presentation and purchase checks, with regression coverage for Hall availability, research compatibility, XP, resources, pending preparation, active doctrines and invalid identifiers. Resource affordability is still rechecked at the native transaction boundary.
- Pending preparations explicitly indicate when the game is paused and must be resumed. Opening the journal preserves the player's existing simulation speed and pause state.

Validation scope and beta limitations are recorded in the accompanying guide. This changelog does not assert publication or universal mod compatibility.

## 0.6.0-beta — veteran journal and Hall

**Runtime acceptance in progress.** This entry describes the implemented beta and recorded checks; it does not confirm publication or complete compatibility. Historical results below keep their original version and binary scope.

- Reworked management into a journal reached through a visible Veterans HUD button and selection-panel access. Army filters cover rank, unit type, specialization and favorites; dossiers include selection/location, names, progress and native specialization confirmation. Added live presentation and English/Spanish language settings plus a diagnostic-report button. The journal uses the native canvas, font, sounds, hints and confirmations, with a guarded input boundary and support for an already-paused game.
- Added soldier identities, sanitized names, favorites, six evidence-based decorations and a bounded service history. Combat contribution and unambiguous eliminations are tracked separately. The memorial records observed deaths, retains the latest 256 soldiers and survives loss of the Hall. Disappearance or world cleanup is not treated as death, and old XP does not fabricate historical achievements.
- Added a Hall of Veterans University replacement with original procedural visuals and a native construction category. It follows mission availability and retains the University construction/worker/repair/research contract. University and Hall share one building slot. A completed ordinary University can be dedicated for free while retaining its entity, researchers, health and native orders. Coverage remains limited to the recorded runtime scenarios, rather than every mission or research combination.
- Added bounded native research aliases for effects that actually target the discovered University. Original research assets and base-object effects remain intact, and explicit Hall effects from another mod take priority. Unsupported identity contracts disable new Hall access; a failed current-world load preflight retains ordinary Universities and skips Hall/doctrine restoration. A hook that skips native research initialization also suspends doctrine effects.
- Added Military tradition (+10% earned XP), Replacement corps (15% less training time) and Veteran recovery (+15% attributed healing for Veteran rank or higher). Unlock milestones are 100/300/600 newly earned player combat XP. Adoption costs normal resources, takes 60 simulation seconds and uses a separate queue. The prior doctrine stays active until its replacement completes; the last Hall's destruction suspends effects and progress.
- Extended exact-request instrumentation to native doctor/allied-healer requests. Four routes now use managed main-thread execution: melee, projectiles, direct-control melee and healing. Healing grants no XP. Historical three-route benchmarks do not measure this change.
- Added clickable promotion notices and favorite/specialization details to selected-veteran insignias. No troop models, armor or weapons are replaced.
- Added schema-3 persistence for the complete campaign state and exact Hall save references. Schemas 1/2 migrate without invented history. Base saves normalize Hall identities to native Universities. Fresh-process Hall restoration and a manual load with both mod DLLs absent were observed; the latter restored the native University model and panel with 1300 health and five researchers.
- Extended the offline save-transfer tool to preserve and validate schema 3, including bounded histories, memorial evidence, duplicate identities, doctrine state and exact building references. Existing no-overwrite, fingerprint, archive and rollback safeguards remain.

Fresh-process isolated loads restored an unfinished Hall with native construction work and a separate completed Hall with pending Recovery preparation. Each matched its one expected saved building reference to one actual Hall; work/preparation continued. Native research raised Hall health from 1000 to 1300. Observed doctrine adoption, suspension after demolition, free dedication, sampled training progress and attributed Doctor healing are recorded separately from prepared QA resources and ranks. A real schema-3 export/import under a new save name on the same PC restored 22 soldiers and the exact Hall reference with Replacements active. This is not a two-computer or full-campaign test.

Automated data-format and lifecycle fixtures cover the new state, alongside bounded runtime and interface checks. Late cancellation or failure of native research initialization disables Hall compatibility while preserving the original exception. No universal compatibility, FPS gain or Nexus publication is claimed for this beta.

## 0.5.0-beta — army tools, specializations and save transfer

- Added an on-demand army roster with counts and average XP by rank, total XP, and current/maximum health. Open it with Ctrl+F9 or the native panel's Army link. Rank buttons select available soldiers across the eligible army; the native group panel's rank cards filter only the current selection. Empty filters preserve selection, and garrisoned soldiers remain in their posts.
- Added one optional, confirmed specialization per friendly soldier from Veteran rank: Assault adds 5 percentage points to the rank damage bonus; Guardian adds 10 percentage points to maximum-health bonus without healing; Mentor multiplies newly earned XP by 1.2. Choices persist individually when persistence is enabled; otherwise they last only for the session. `Specializations.Enabled=false` suspends effects without erasing choices. Sidecar schema 2 stores the choice; schema 1 remains readable as unassigned.
- Added an explicit local conflict report through Ctrl+F10 or the roster button. It inspects bounded DLL metadata and visible Harmony owners/priorities, flags duplicate assemblies and potential hook overlap, and excludes private paths, configuration and save data. It uploads nothing and does not certify compatibility. Both new shortcuts are configurable.
- Added `Transfer-VeteranSave.ps1` and `SAVE-TRANSFER.md` to the manual package. The offline Windows tool exports a matching save/header/sidecar, verifies the bundle, previews import, then creates a new save name without overwriting existing files. It preserves schema-2 choices while rebinding the sidecar's path identity. It does not convert game versions or authenticate arbitrary save files.
- Prepared health-request/source archetypes for all three instrumented combat paths, avoiding an added-component structural change per hit. Source attribution and the three-path coverage remain intact; the jobs still run as managed main-thread work. No FPS gain is claimed from the code change alone.
- Made save normalization publish a complete validated copy. Invalid XP/specialization with safe health omits that progression record while removing its recorded health factor. Unsafe health or normalization abandons the whole prepared batch; vanilla saving continues with its captured statistics and no new sidecar. This can retain a veteran bonus in a corrupt-data fallback and is not an absolute save-recovery guarantee.

Initial isolated Unity QA passed real projectile XP before injection, independent idle-soldier XP, controlled specialization/health/selection cases, and exact-reference save/load in one process. The saved Guardian restored with 931 XP, 25.875 current health and a 1.3 owned health factor. Army UI, an eight-unit selection and the local report result were inspected. Actual entity-index reuse was not observed. Synthetic requests and test-assigned progress are identified separately from real projectile evidence.

A real export/import under a new save name then passed six checks in a fresh isolated process with Resource Fairy present, restoring the exact saved reference, 931 XP, 25.875 current health, factor 1.3 and Guardian. This used a later QA binary; it is a transfer/restart check, not a repeat of 0.4's combined combat scenario or a test of Resource Fairy's economic effects.

Later English UI checks passed at 1080p/720p: Ctrl+F9, choose/confirm, cancellation on selection change, Assault's displayed damage change from 5.75 to 6 on an Elite with 900 XP, army rank selection of two Veterans, a native five-archer filter retaining one Veteran, and F8 hide/restore. These used fixture-assigned XP and demonstrate interaction/rendering, not naturally earned progress.

Six benchmark runs compared three alternating pairs of the legacy 0.4 and prepared 0.5 patchers with the same 0.5 QA runtime, configuration and 1,000-unit prepared arena at 3840×2160 on an i7-13700KF/RTX 4080. All comparability checks passed. Median per-run mean frame time was 12.3264→12.3061 ms (−0.16%); median p95 was 13.6490→13.5012 ms (−1.08%). The mean ranges overlap and this small sample does not establish statistical significance or a reliable FPS gain. These are whole-frame measurements of the patcher change, not complete-release 0.4-versus-0.5 or isolated CPU/allocation measurements.

Gameplay and interface evidence retains the scope of its recorded QA runs. Resource Fairy combat results below retain their historical 0.4 scope.

## 0.4.0-beta — stability and promotion notices

- Made repeated XP/snapshot restoration preserve the soldier's existing health-factor ownership. Interrupted cleanup can retry without removing the same factor twice; invalid snapshots and disabled-runtime restores cannot partially write health.
- Moved sidecar verification ahead of the game's load-complete publication and associated callbacks with their originating save/load system. Errors in the mod's load hooks are contained so they do not throw through the original load operation.
- Made preloader changes transactional: validate and serialize a copied assembly before committing; a rejection preserves the incoming assembly and allows the loader to continue. Added explicit health-hook ordering and handling for skipped originals.
- Added grouped, non-interactive notices for friendly combat promotions, with English/Spanish text, bounded batching and no notices when restoring saved progress.
- Added `Interface.ShowPromotionNotifications` and configurable `Interface.ToggleInterfaceShortcut` (`F8` by default; supports modifiers or `None`). Recognized text fields suppress the shortcut, and hidden/paused/menu sessions discard old notices.
- Expanded health/persistence, preloader-conflict and Harmony-order fixtures. The Harmony suite uses the supplied loader library on .NET Framework 4.8; the public runtime still targets Unity Mono through `netstandard2.0`.

Final public and QA builds each passed 509 checks (42 progression/localization/notice, 34 persistence/health, 412 patcher and 21 Harmony checks), with no build warnings or errors. Earlier isolated Unity sessions, standalone and with Resource Fairy (download 0.0.7/internal 0.0.6), each passed 19 checks and recorded one separate index-recycling-not-observed entry: real projectile XP, an idle soldier without XP, repeated restoration, a reversible simulated ×1.5 modifier and exact-reference save/load. A fresh-process reload with both plugins passed five checks at 931 XP, 23.875 health and factor 1.2. The simulated health modifier was not a Resource Fairy effect; its configured money ×2 was not stimulated or measured, and Resource Fairy alone was not tested.

English individual/group promotion cards were reviewed at 1080p/720p. Ctrl+F7 hid/restored visuals with F8 free, and hidden/menu states discarded notices. A 720p insignia/card overlap was corrected and visually rechecked in the final Spanish build. Final Spanish review covered an individual notice at 720p, a 12-promotion card and group selection at 1080p, and the individual Legendary panel at 1080p. Individual notices direct players to inspect actual unit stats instead of displaying potentially capped nominal bonuses. The final rebuild's fixture suites passed again; earlier gameplay evidence retains its own binary identity.

BepInEx logs had no Error/Fatal, while Unity Player logs contained `StudioAds.LoadRemoteData` and `WindowsVideoMedia` errors in combined and visual-without-Resource-Fairy sessions. The cause remains undetermined despite continued execution. That 0.4 update introduced no new performance or universal mod compatibility claim. The 0.3.0-beta archive and its historical evidence remain unchanged.

## 0.3.0-beta — public beta

- Prepared a manual game-root package containing the two mod DLLs, English documentation and a separate reference configuration that does not overwrite existing settings.
- Added English mod labels by default and an optional Spanish interface, preserving custom rank names.
- Separated the public build from the internal functional, visual and benchmark harnesses. Test fixtures are not part of the player release.
- Added explicit game-build requirements, AI disclosure, dependency information, content hashes and public support instructions.

The earlier 0.1.0/0.2.0 evidence below retains its original scope. It must not be read as a complete gameplay regression or performance measurement of a different public binary.

## 0.2.0 — 18 October 2026

- Integrated individual and group veterancy information into the game's native selection panel, replacing the earlier floating panel.
- Added five distinct shared rank shields and bounded insignias over selected veterans. No troop meshes, clothing or equipment were replaced.
- Preserved native selection filters and added group rank counts and total XP.
- Added shared F8 visibility and separate panel/insignia settings.
- Corrected native-stat restoration, research colors and group damage display to follow the game's panel semantics for supported temporary skills.
- Added an isolated, explicitly enabled visual fixture for testing. Its assigned ranks are synthetic presentation data, not evidence of XP earned in combat.

Validation: clean build with 422 calculation/persistence/instrumentation checks; installer, loader, CLI and package tests; visual checks at 720p and 1080p, plus an individual panel with the game at 4K; five checks restoring a 0.1.0 test save in a fresh process. The final local build was installed in the original game and reached its menu without loading personal saves.

A one-pair interface comparison used 1000 combatants and 500 selected units observed as recruits: 85.98 FPS with the interface versus 87.04 without it. This is not a total-mod benchmark, a worst-case insignia benchmark or a broad performance guarantee. See the user guide for context.

## 0.1.0 — 18 October 2026

- Added individual XP, five configurable ranks and personal damage/maximum-health bonuses.
- Instrumented normal melee, projectile impacts and direct-control melee on the supported game build.
- Added conservative kill credit and effective-damage XP without proximity rewards.
- Added save-linked progression sidecars and exact saved-unit identity restoration.
- Added the initial selection panel and local installation/removal tooling.

Real projectile XP and independence between two archers were validated. Additional controlled tests covered damage limits, rank bonuses, death and save/reload behavior. A fresh-process reload preserved the tested unit's identity, XP and health. Those results do not certify every troop, ability or campaign. Three combat jobs run as managed main-thread work; the performance cost remains a known limitation.
