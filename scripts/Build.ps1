[CmdletBinding()]
param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Diplomacy is Not an Option', [switch]$SkipTests, [switch]$IncludeTestHarness)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSdk = Join-Path $taskRoot 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) {
    $taskDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $taskDotnet) { throw 'A .NET 8 SDK is required. See docs/DEVELOPMENT.md.' }
    $taskSdk = $taskDotnet.Source
}
$env:DOTNET_CLI_HOME = Join-Path $taskRoot 'tools\.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $taskRoot 'tools\nuget'
$env:DOTNET_ROOT = Split-Path -Parent $taskSdk
$taskHarnessValue = $IncludeTestHarness.IsPresent.ToString().ToLowerInvariant()
$taskBinarySubdirectory = if ($IncludeTestHarness) { 'bin\Release\qa\netstandard2.0' } else { 'bin\Release\netstandard2.0' }
$taskTestSubdirectory = if ($IncludeTestHarness) { 'bin\Release\qa\net8.0' } else { 'bin\Release\net8.0' }
$taskBuildOutput = if ($IncludeTestHarness) { 'build\qa' } else { 'build' }
Push-Location $taskRoot
try {
    & $taskSdk build 'VeteranSoldiers.sln' -c Release --nologo "-p:GamePath=$GamePath" "-p:IncludeTestHarness=$taskHarnessValue"
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    if (-not $SkipTests) {
        foreach ($taskTest in @('tests\Progression.Tests\Progression.Tests.csproj','tests\Persistence.Tests\Persistence.Tests.csproj','tests\VeteranSoldiers.Patcher.Tests\VeteranSoldiers.Patcher.Tests.csproj','tests\Compatibility.Tests\Compatibility.Tests.csproj','tests\Diagnostics.Tests\Diagnostics.Tests.csproj','tests\Hall.Tests\Hall.Tests.csproj','tests\Chronicle.Tests\Chronicle.Tests.csproj','tests\HallResearch.Tests\HallResearch.Tests.csproj')) {
            if (Test-Path -LiteralPath $taskTest) {
                $taskTestName = [IO.Path]::GetFileNameWithoutExtension($taskTest)
                $taskTestAssembly = Join-Path (Split-Path -Parent $taskTest) "$taskTestSubdirectory\$taskTestName.dll"
                if ($taskTest -like '*Compatibility.Tests*' -or $taskTest -like '*Diagnostics.Tests*' -or $taskTest -like '*HallResearch.Tests*') {
                    $taskCompatibilityAssembly = Join-Path (Split-Path -Parent $taskTest) ($taskTestSubdirectory.Replace('net8.0', 'net48') + '\' + $taskTestName + '.exe')
                    & $taskCompatibilityAssembly
                } elseif ($taskTest -like '*Patcher.Tests*') {
                    $taskManaged = Join-Path $GamePath 'Diplomacy is Not an Option_Data\Managed'
                    $taskRuntime = Join-Path $taskRoot "src\VeteranSoldiers\$taskBinarySubdirectory\VeteranSoldiers.dll"
                    & $taskSdk $taskTestAssembly $taskManaged $taskRuntime
                } else {
                    & $taskSdk $taskTestAssembly
                }
                if ($LASTEXITCODE -ne 0) { throw "Test failed: $taskTest" }
            }
        }
    }
    New-Item -ItemType Directory -Path $taskBuildOutput -Force | Out-Null
    foreach ($taskProject in @('VeteranSoldiers','VeteranSoldiers.Patcher')) {
        Copy-Item -LiteralPath "src\$taskProject\$taskBinarySubdirectory\$taskProject.dll" -Destination $taskBuildOutput
    }
    Write-Output $(if ($IncludeTestHarness) { 'QA build: isolated test harnesses included; never publish these DLLs.' } else { 'Public build: test harnesses excluded.' })
    Get-FileHash -LiteralPath (Join-Path $taskBuildOutput 'VeteranSoldiers.dll'),(Join-Path $taskBuildOutput 'VeteranSoldiers.Patcher.dll') | Format-Table -AutoSize
} finally { Pop-Location }
