# Source review map

## Entry points and dependency boundary

- `src/VeteranSoldiers.Patcher/Patcher.cs`: BepInEx preloader entry point and strict compatibility checks.
- `CombatPatcher.cs`, `MedicalPatcher.cs`, `AssemblyPatchTransaction.cs` and `PreparedRequestArchetype.cs`: Mono.Cecil transformations of four supported game jobs in memory and transactional failure handling. This does not distribute or rewrite the game's DLLs on disk.
- `src/VeteranSoldiers/VeteranRuntime.cs` and `Bootstrap.cs`: runtime ECS/Harmony hooks and lifecycle.
- `Progression.cs`, `CombatBridge.cs`, `VeteranArmy.cs` and `VeteranHealth.cs`: attributable effective hostile damage, experience, exact entity identity and idempotent health modifiers. Proximity does not award XP.
- `VeteranHall.cs`, `HallRules.cs`, `VeteranHallResearch.cs` and `HallVisuals.cs`: Hall, doctrine rules, native University research boundary and procedural building geometry.
- `VeteranNativePanel.cs`, `VeteranArmyWindow.cs`, `VeteranUiBridge.cs`, `VeteranVisualAssets.cs` and `VeteranWorldInsignias.cs`: interface integration and procedural visuals. Native game interface resources are obtained from the player's installation at runtime; game assets are not bundled.

## Persistence and local file access

- `Persistence.cs`, `PersistenceModels.cs`, `ChronicleXml.cs` and `PortableSave.cs`: legacy sidecar handling and bounded, checksummed XML attached to the native save. Exact saved-unit references and content checks prevent guessing identity from `Entity.Index` alone. The native serialized prefix is retained.
- `SaveCaptureBatch.cs`: save capture and restoration boundaries.
- `ModSettings.cs`: BepInEx configuration.
- `CompatibilityDiagnostics.cs`: explicitly requested local diagnostic reports, with bounded output and privacy filtering.
- `scripts/Transfer-VeteranSave.ps1`: optional, separately invoked offline import/export tool with explicit source/destination and validation. Its exact shipped copy and user documentation are under `player-package/`.

The mod reads game/runtime configuration and saves, and writes its own settings, save-side progress/backup data and requested local reports. It does not request credentials or upload saves, telemetry or diagnostics. See the cited source paths for exact checks and paths. Build/test package restore may use NuGet; it is separate from the runtime mod.

## Source coverage and exclusions

The snapshot includes every own source file needed for both submitted assemblies and the optional script shipped in Nexus file 31, the original solution/build entry point, all eight solution test projects, the transfer tool's tests and the exact text/metadata entries from the public ZIP. `SOURCE-MANIFEST.json` identifies each file and origin. The original package-content manifest under `player-package/` describes the frozen **binary package**, not the contents of this source repository.

Development-only game harnesses are included as source and excluded from public compilation. They are not a claim of portable standalone game tests. The separate personal USB installer and unrelated developer orchestration, screenshots and upstream reference trees are outside the submitted mod and this review snapshot.

Third-party loader files, proprietary assemblies/decompilation, compiled outputs, private saves, live settings, credentials, correspondence and earlier Git history are not distributed. Build dependencies under `tools/` and generated test files are ignored. The original source-review ZIP's old preparation-only notice is superseded by these instructions; this repository does not contain that stale notice.

## Attribution, AI disclosure and permissions

**Kirapp** is the creator and system designer. **OpenAI Codex** contributed substantially to implementation, UI, procedural visual code, debugging, tests and documentation under Kirapp's direction. The work is not represented as wholly human-written. The Nexus listing's relevant disclosures are **AI-Generated Content** and **AI Media**, as applicable under the [Nexus submission guidelines](https://help.nexusmods.com/article/28-file-submission-guidelines).

The original [credits and permissions](player-package/CREDITS_AND_PERMISSIONS.md) remain included verbatim, including historical version references. Source inspection does not grant a new reuse, public redistribution or port license, and cannot grant rights in third-party/game material. No particular quarantine cause, false-positive verdict or moderation outcome is claimed.
