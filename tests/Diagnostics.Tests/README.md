# Compatibility diagnostics fixtures

This project links the production `CompatibilityDiagnostics.cs` and uses the installed loader's HarmonyX on .NET Framework 4.8. It does not start Unity or load a third-party mod. Only the minimal `VeteranRuntime` constants are stubbed.

Run from the repository root:

```powershell
$env:DOTNET_ROOT = (Resolve-Path tools/dotnet).Path
$env:DOTNET_CLI_HOME = (Resolve-Path tools/.dotnet-home).Path
& tools/dotnet/dotnet.exe build tests/Diagnostics.Tests/Diagnostics.Tests.csproj -c Release -p:UseArtifactsOutput=true "-p:ArtifactsPath=$PWD/artifacts/diagnostics"
& artifacts/diagnostics/bin/Diagnostics.Tests/release/Diagnostics.Tests.exe
```

The executable tests actual Harmony owner discovery, priorities and relationships, overlapping prefixes/transpilers, renamed duplicate DLLs, duplicate plugin GUIDs, malformed metadata, scan bounds, metadata privacy, and report writing/preservation. It verifies that inspection does not instantiate plugin/attribute constructors, load the duplicate files into the CLR, change registrations, or read configuration into the report. Its private file fixtures remain under ignored `logs/diagnostics-tests-*` for inspection. No solution or global build integration is included by this subtask.

Production integration: call `CompatibilityDiagnostics.TryWriteReport(out string fileName, out string failureCode)` only on an explicit button/shortcut action. It returns a basename on success. The report is stored under the existing BepInEx configuration directory, in `VeteranSoldiers/diagnostics`. No automatic polling, upload or config modification occurs. Requests have a five-second cooldown. Error codes are fixed: `BUSY`, `COOLDOWN`, `LINKED_OUTPUT_DIRECTORY`, `ACCESS_DENIED`, `IO_FAILURE`, `NOT_MANAGED_DLL`, or `INSPECTION_FAILED`.

The report uses logical DLL IDs and sanitized assembly/plugin/Harmony identifiers. It omits actual filenames, directories, plugin display/author names, machine/user names, configuration values, saves and exception messages. Arbitrary metadata with path, email, credential-like or control-character content is redacted. Disk DLL inspection uses `AssemblyName.GetAssemblyName` and hashing, never `Assembly.Load`/`LoadFrom`. Plugin metadata is read only from assemblies already loaded by the host, with `CustomAttributeData`.

Limits: 512 files, 256 directories, depth six, 64 MiB hashed per file / 128 MiB total, and a soft five-second inspection budget. A blocking filesystem call cannot be interrupted by that budget. Links are skipped during scanning, and linked output directories are refused; junction behavior is reviewed in code, not exercised by these fixtures. Truncation and unreadable metadata are reported as partial inspection. Harmony from another isolated library, native detours, original preloader history and runtime statistic writes may not be observable. Priority declarations do not establish final execution order. Neither a warning nor an empty warning list is a compatibility verdict. A game-level test of the caller and generated report remains separate.
