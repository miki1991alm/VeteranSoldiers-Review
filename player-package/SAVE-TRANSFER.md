# Transfer a save with its Veteran Soldiers progress

**Legacy transfer utility, retained in 1.0.1.** Newly saved 1.0.1 slots carry their own checksummed progression inside the native save and no longer need this utility just to change computers. Install 1.0.1 on both PCs, save and exit on the source, let Steam finish synchronizing, then load on the destination. For old slots, update the PC where the correct levels still exist, load the original matching save/sidecar and save under a new name first. Updating only the receiving PC cannot recover missing historical XP.

This utility still requires a matching local sidecar; it remains useful for legacy slots and complete private backups. Schema-3 parsing, transfer and rollback have automated fixture coverage. Historical 0.6 original-path Hall loads and 0.5 transfer results retain their original binary scope.

`Transfer-VeteranSave.ps1` moves one saved game, its menu header and its matching veterancy data between folders or computers. It copies files; it does not move or remove the source. Import creates a **new save name** and never overwrites an existing save, header or sidecar.

Use Windows PowerShell 5.1 or PowerShell 7 on Windows. No compiler, game assemblies, Internet connection or administrator rights are needed by the tool. The receiving game still needs the compatible Veteran Soldiers mod and its ECS loader.

This transfer format targets Steam build `25478044` / `dno_1.0.187_r`. It accepts sidecar schemas 1, 2 and 3. Schema 2 includes specialization; schema 3 also includes names, favorites, service histories, decorations, memorial, campaign counters, exact Hall building references and doctrine state. The receiving mod must understand the source schema; use 0.6 or a later compatible version for schema 3. It does not convert a game save to a different game version or downgrade a sidecar. The game binaries remain opaque: a matching hash proves consistency, not authenticity or that a save is safe to load. Transfer your own saves or files from someone you trust. Bundles contain private game progress and chosen soldier names; do not include them in a mod release or a public bug report.

## 1. Prepare both computers

1. Save the source game with Veteran Soldiers enabled and persistence active. Allow the save to finish, then exit the game normally.
2. On the receiving computer, install the compatible game, ECS loader and mod. Create or select the receiving profile in the game, then exit normally.
3. Keep backups of your game saves and `BepInEx/config/VeteranSoldiers/saves`. Keep both copies of the game closed during the transfer. The tool refuses to export/import while a process named `Diplomacy is Not an Option` is running; it never closes it for you.
4. Find the game installation with Steam's **Manage → Browse local files**. This is the folder containing `BepInEx/config`.
5. Find the profile containing the source save. On a normal Windows installation it is inside `%USERPROFILE%\AppData\LocalLow\Door 407\Diplomacy is Not an Option\DNOPersistentData`. Select the particular profile subfolder, not `DNOPersistentData` itself. An isolated test copy can use a different LocalLow identity.

A saved slot consists of a file without an extension and a companion with the same name followed by `.dat`. The tool locates its matching XML sidecar automatically from the save's full path. Do not rename the source save before exporting.

## 2. Export on the source computer

Open PowerShell in the directory containing the distributed script. Replace the example paths with your folders. The output directory must already exist, and the bundle filename must be new.

```powershell
$game = 'D:\Games\Diplomacy is Not an Option'
$save = 'D:\MyProfiles\DNOPersistentData\Player\MySave'
$bundle = 'D:\Transfers\MySave.veterans.zip'

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Transfer-VeteranSave.ps1 `
  -Mode Export -GamePath $game -SavePath $save -BundlePath $bundle
```

Use the full path to the extensionless save for `-SavePath`, not the `.dat` file. `Exported` reports the bundle SHA-256, number of veteran records and sidecar schema. The source files are opened without permitting concurrent writes and remain unchanged. If there is no matching sidecar, export stops instead of silently dropping XP. Save again with the mod active and wait for its completed sidecar before retrying.

The bundle has four fixed entries:

| Entry | Content |
|---|---|
| `save.bin` | Exact bytes of the game's extensionless save |
| `save.dat` | Exact bytes of its menu header |
| `veterans.xml` | Matching complete sidecar: progression and specialization, plus schema-3 soldier and campaign state when present |
| `manifest.json` | Format, supported target build, payload sizes/hashes and the hashed source-path identity |

The manifest does not contain your source path, Windows account, profile name or save filename. The opaque game save/header may themselves contain personal progress or names. The bundle is not encrypted or digitally signed.

Copy the bundle to the other computer using your usual file-transfer method. If it passed through an untrusted channel, compare its reported SHA-256 with the source result through a trusted channel.

## 3. Inspect and preview on the receiving computer

Inspection verifies the ZIP, manifest, hashes and XML without creating destination files:

```powershell
$bundle = 'D:\Transfers\MySave.veterans.zip'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Transfer-VeteranSave.ps1 `
  -Mode Inspect -BundlePath $bundle
```

Choose the receiving game folder, an existing profile and a new save name. Letters, digits, spaces, underscores and hyphens are accepted, with no extension or leading/trailing spaces. Import first previews the three destinations:

```powershell
$game = 'D:\Games\Diplomacy is Not an Option'
$profile = 'D:\MyProfiles\DNOPersistentData\ReceivingPlayer'
$name = 'ImportedVeterans'

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Transfer-VeteranSave.ps1 `
  -Mode Import -BundlePath $bundle -GamePath $game `
  -ProfilePath $profile -SaveName $name
```

The `Preview` result makes **zero writes**, including no directory creation. Check that `SavePath`, `HeaderPath` and `SidecarPath` point to the intended installation and profile.

## 4. Apply the import and load it

Repeat the same import command with `-Apply`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Transfer-VeteranSave.ps1 `
  -Mode Import -BundlePath $bundle -GamePath $game `
  -ProfilePath $profile -SaveName $name -Apply
```

`Imported` means three new files were written and their hashes verified. The save and `.dat` remain byte-identical to the exported files. The XML keeps each saved-unit reference, object type, XP, health and specialization. Schema 3 also retains soldier identities, names, favorites, history, medals, memorial, campaign counters, Hall references and active/pending doctrine progress. Only its root `pathSha256` and sidecar filename are rebound to the exact new save path. XML formatting can change. The source schema is preserved: schema 1 remains schema 1 and means no specialization; schemas 1/2 do not acquire invented histories or Hall state.

Start the receiving game, select that profile and load the new save through **Load**. Select a veteran to check its rank, XP and specialization. For schema 3, inspect its name/history, the memorial and any saved Hall/doctrine progress. The runtime still validates the actual saved-unit and building mappings; the transfer tool does not replace those checks. Keep the original bundle and backups until satisfied with the result. Merely copying or renaming the files again will break the path binding; export/import again to move them.

The tool does not change the current profile selector, campaign unlocks, global settings, PlayerPrefs, Steam account, Steam Cloud configuration or any mod configuration. It does not migrate another mod's private files. Steam Cloud does not automatically synchronize Veteran Soldiers sidecars; use a new export when transferring later progress.

## Failures and recovery

- **An existing destination:** choose a different `-SaveName`. Nothing is overwritten, even if existing bytes appear identical.
- **Missing `.dat` or sidecar:** restore the matching companion from a backup or save again with the mod enabled. The tool does not invent a header or XP.
- **Fingerprint mismatch:** the sidecar belongs to different save bytes or another source path. Do not edit the hashes to force a match; locate the original matching set.
- **Unknown schema/build target:** use the tool released with the compatible mod. Never strip unknown fields to make import pass.
- **Closed-game check or file lock:** exit normally and allow saving/synchronization to finish, then retry. No processes are terminated and no registry or Cloud settings are changed.
- **Link/junction refusal:** use ordinary local folders. Network paths, symbolic links, reparse points, Windows device names and alternate data streams are unsupported.
- **Import I/O failure:** ordinary exceptions trigger rollback of files created by that invocation. Files changed by another process after creation are preserved rather than deleted. Review the named destinations before retrying if a warning is reported.

Import stages private temporary files and publishes the sidecar/header before the game save. This reduces the chance of the menu seeing an incomplete slot. A three-file transfer is not an operating-system transaction: power loss, forced termination or a simultaneous external filesystem change can leave partial files. The game should remain closed. Back up leftovers before manually removing only the new slot/header/sidecar identified by the preview; never delete the entire profile or BepInEx folder.

Limits: save 256 MiB, `.dat` 16 MiB, sidecar 32 MiB, manifest 16 KiB, archive 320 MiB, and 100,000 veteran records. Schema 3 additionally bounds names to 28 characters, histories to 12 events per soldier, memorial entries to 256 and Hall references to 128. The Hall-reference bound is a defensive storage limit, not permission to construct extra Halls: the normal game shares one building slot between the University and Hall. Only four exact archive names are allowed. Duplicate archive entries, traversal, nested paths, links, excessive expansion, DTDs, non-finite/invalid health or XP and unsupported specialization values are rejected. Schema 3 rejects duplicate soldier identities, contradictory memorial events, unsupported or locked active/pending doctrines, invalid progress and duplicate building references. These checks establish bounded parsing and internal consistency; they do not authenticate arbitrary game data or guarantee compatibility with every mod.

## Developer validation

Run the fixture suite from the source checkout:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File .\tests\Test-SaveTransfer.ps1
```

The suite creates synthetic, deliberately non-playable save/header bytes only under `sandbox/save-transfer-*`. The 0.6 implementation passed 68 checks covering all three schemas, path rebinding, exact byte preservation, specialization, complete campaign transfer, rejection cases, preview/no-overwrite behavior, and late-failure rollback preserving an unrelated collision file. A debugger breakpoint injects that collision at the real commit statement; the distributed tool has no test bypass. Separate `powershell.exe -File` invocations check the Windows PowerShell 5.1 entry point for both the legacy and schema-3 formats. These fixtures do not claim a game load; a real isolated transfer/load must be recorded separately before claiming that integration was tested.
