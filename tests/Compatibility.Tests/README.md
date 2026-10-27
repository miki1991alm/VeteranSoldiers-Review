# Harmony coexistence regression fixture

This executable links the actual `Bootstrap.cs` and `Progression.cs` sources. It
uses the same local HarmonyX, MonoMod and Cecil libraries as the required BepInEx
ECS loader. Small Unity, BepInEx and health-system doubles let it exercise the
production hook registration without loading or starting the game.

The 21 checks cover:

- capture at `Priority.Last` and observation at `Priority.First`;
- dependency completion around one original health update;
- another Harmony ID editing one damage request and zeroing another;
- another prefix cancelling the original, in both installation orders;
- another postfix healing after the observed damage, in both orders;
- an explicit `after` constraint cancelling after capture, with no credit;
- fresh state on the next invocation after that cancellation;
- disabled runtime, failed capture, missing state and different worlds.

Run on Windows with .NET Framework 4.8 and its reference assemblies installed:

```powershell
& .\tools\dotnet\dotnet.exe build .\tests\Compatibility.Tests\Compatibility.Tests.csproj -c Release
& .\tests\Compatibility.Tests\bin\Release\net48\Compatibility.Tests.exe
```

The SDK compiles the fixture; the executable runs on CLR 4. The loader's existing
HarmonyX fails during initialization on CoreCLR 8, so this project deliberately
does not replace it with a different Harmony version just to run a test. An
`IncludeTestHarness=true` build uses `bin\Release\qa\net48`.

These are managed fixture checks, not Unity/job-safety or gameplay tests. Priority
does not override explicit `before`/`after` dependencies, and equal priorities
still depend on installation order. A foreign prefix explicitly placed after our
capture can already encounter boosted requests; the postfix guard prevents XP
when that mod then cancels the original, but does not undo request edits. Mods
that replace the original processing or rewrite it with transpilers still need
individual review and runtime testing.
