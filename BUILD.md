# Build Veteran Soldiers 1.0.1

These instructions build the own-source mod and run code tests. They do not launch, install into or modify the game. No account credentials, API keys or cloud connection are required to build.

## 1. Prerequisites

Use Windows x64 and PowerShell 5.1 or later, with:

1. **.NET SDK 8.0.426**, the SDK used for the frozen submission and source-review validation. Install it from Microsoft's [.NET 8 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), or extract that SDK under `tools/dotnet/` in this checkout. `global.json` selects this SDK. A newer SDK alone is not the recorded toolchain.
2. **.NET Framework 4.8 or later runtime** to execute the three `net48` test programs. If the build reports missing .NET Framework reference assemblies, install Microsoft's [4.8 developer pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48). NuGet restore also requires network access for the projects' reference packages.
3. A legitimate installation of *Diplomacy is Not an Option*, supported version **dno_1.0.187_r**, Steam build **25478044**, Unity **2021.3.45f2 Mono**, Windows x64. This source references its assemblies locally. Obtain them from your licensed game; they are not supplied here.
4. **BepInEx 5.4.23.2 with Unity ECS Support**, obtained independently from its [author's Nexus page](https://www.nexusmods.com/diplomacyisnotanoption/mods/1). Ordinary BepInEx is not an equivalent dependency. The original loader archive's SHA-256 is `4402C22962E66353A592483990A7F9D71F8A5F5D10B95EFAF52A6EB2C373956A`.

Extract the loader's entire `BepInEx/core` directory to:

```text
<checkout>/tools/bepinex-ecs/BepInEx/core/
    BepInEx.dll
    0Harmony.dll
    Mono.Cecil.dll
    MonoMod.RuntimeDetour.dll
    MonoMod.Utils.dll
    ...the other files from the original core directory
```

This is a local build-reference directory, not an instruction to install the loader into the game. `tools/` and all build/test outputs are Git-ignored and must never be committed or included in a source upload.

## 2. Confirm the game reference

Open PowerShell in the checkout root. Set the following variable to your own game installation; the example is Steam's default location:

```powershell
$reviewGame = 'C:\Program Files (x86)\Steam\steamapps\common\Diplomacy is Not an Option'
$reviewMain = Join-Path $reviewGame 'Diplomacy is Not an Option_Data\Managed\DNO.Main.dll'
(Get-FileHash -LiteralPath $reviewMain -Algorithm SHA256).Hash
```

The expected hash is:

```text
098B19C4580FF1C5CE4AA62D78E04BEF0429C2632EC4C36E4299270EE3CA3987
```

A later Steam update may change these assemblies. Do not treat a successful compile against another build as verified runtime compatibility. The patcher and IL tests deliberately check the supported game contract.

## 3. Build Release and run the eight suites

From the checkout root, with the prerequisite SDK on `PATH` or in `tools/dotnet/`:

```powershell
& '.\scripts\Build.ps1' -GamePath $reviewGame
```

`Build.ps1` builds `VeteranSoldiers.sln` with `Configuration=Release` and `IncludeTestHarness=false`, restores reference packages, executes all eight solution test programs, and copies these two outputs only after the tests pass:

```text
build/VeteranSoldiers.dll
build/VeteranSoldiers.Patcher.dll
```

The assemblies target `netstandard2.0`. The progression, persistence, IL patcher, Hall rules and chronicle tests run on .NET 8. The compatibility, diagnostics and Hall research fixtures run directly on .NET Framework 4.8, using the required loader's Harmony/MonoMod assemblies. Do not execute the `net48` test executables through `dotnet`, or substitute ordinary BepInEx/Harmony to force them to run.

The script prints build diagnostics, individual test results and output hashes. The recorded 1.0.1 baseline has **2,857 assertions** across these suites, including per-instruction IL assertions. This is not 2,857 gameplay scenarios. `-SkipTests` omits validation and is not the review command above.

CLI home, NuGet packages, build outputs and test fixtures are created within this checkout. The SDK, Windows runtime and game references remain external prerequisites. Persistence and diagnostics fixtures use the checkout's `logs/`; transfer fixtures use `sandbox/`.

## 4. Optional transfer utility

The public ZIP's `VeteranSoldiers/Transfer-VeteranSave.ps1` is included unchanged in both `scripts/` and `player-package/`. It is an interpreted PowerShell utility and has no compiled output or network dependency. To run its fixture-based regression tests:

```powershell
& '.\tests\Test-SaveTransfer.ps1'
```

These tests create synthetic save bundles under `sandbox/`; they do not read personal game saves or prove Steam Cloud synchronization. Instructions for real transfers are in [player-package/SAVE-TRANSFER.md](player-package/SAVE-TRANSFER.md). The mod does not automatically run this utility.

## 5. Build identity and limits

The frozen binaries use assembly version **1.0.1.0**. Their informational metadata includes the then-current local Git base `6036db7e38a11b0f1d664d5c3b02f21c43f6a8e7`; the implementation was subsequently committed as `a3cae5f77d3205c50ba06588483573eb5a8e9dce`. Building in a new review repository or outside Git may produce different informational metadata and hashes. The source snapshot does not claim that an ordinary rebuild is byte-identical to the frozen file 31 binaries.

`RuntimeSelfTest`, `RuntimeVisualTest`, `RuntimeBenchmark` and `RuntimeHallTest` sources are present for inspection but explicitly excluded from public compilation by `VeteranSoldiers.csproj`. Enabling `-IncludeTestHarness` produces engineering binaries for a separately isolated game copy; it is not part of these review instructions. Do not run those harnesses against personal profiles.

Passing these tests validates the stated managed fixtures and IL contracts. It does not certify gameplay, every other mod, future game updates, an independent two-PC Steam Cloud round trip or Nexus moderation approval. Rebuilding for review does not replace or re-upload the frozen submission.
