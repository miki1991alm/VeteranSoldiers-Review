#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$testRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testTool=Join-Path $testRoot 'scripts\Transfer-VeteranSave.ps1'
$testFixture=Join-Path $testRoot ('sandbox\save-transfer-'+[Guid]::NewGuid().ToString('N'))
$testUtf8=[Text.UTF8Encoding]::new($false)
$testPasses=[Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-Test([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Pass([string]$Name) { $testPasses.Add($Name) }
function Test-Hash([byte[]]$Bytes) { $sha=[Security.Cryptography.SHA256]::Create(); try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() } }
function Path-Hash([string]$Path) { return Test-Hash $testUtf8.GetBytes([IO.Path]::GetFullPath($Path).ToUpperInvariant()) }
function Fixture([string]$Relative) {
    $path=[IO.Path]::GetFullPath((Join-Path $testFixture $Relative))
    Assert-Test ($path.StartsWith($testFixture+'\',[StringComparison]::OrdinalIgnoreCase)) 'Fixture escaped workspace.'
    return $path
}
function Write-Text([string]$Path,[string]$Text) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null; [IO.File]::WriteAllText($Path,$Text,$testUtf8) }
function Refuses([scriptblock]$Action,[string]$Name,[string]$Pattern='') {
    $failed=$false
    try { & $Action | Out-Null } catch { $failed=$true; if ($Pattern) { Assert-Test ($_.Exception.ToString() -match $Pattern) ('Wrong refusal for '+$Name+': '+$_.Exception.Message) } }
    Assert-Test $failed ('Expected refusal: '+$Name); Pass $Name
}
function Read-TestZip([string]$Path) {
    $zip=[IO.Compression.ZipFile]::OpenRead($Path); $data=@{}
    try { foreach ($entry in $zip.Entries) { $input=$entry.Open(); $memory=[IO.MemoryStream]::new(); try {$input.CopyTo($memory);$data[$entry.FullName]=$memory.ToArray()} finally {$input.Dispose();$memory.Dispose()} } }
    finally {$zip.Dispose()}
    return $data
}
function Write-TestZip([string]$Path,$Data,[string]$ExtraName='', [int]$LinkAttributes=0, [switch]$Compressed, [string]$ReplaceSaveName='') {
    $zip=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('manifest.json','save.bin','save.dat','veterans.xml')) {
            $compression=if ($Compressed) {[IO.Compression.CompressionLevel]::Optimal} else {[IO.Compression.CompressionLevel]::NoCompression}
            $entryName=if ($name -ceq 'save.bin' -and $ReplaceSaveName) {$ReplaceSaveName} else {$name}
            $entry=$zip.CreateEntry($entryName,$compression)
            if ($name -ceq 'save.bin') {$entry.ExternalAttributes=$LinkAttributes}
            $stream=$entry.Open();try {$stream.Write($Data[$name],0,$Data[$name].Length)}finally{$stream.Dispose()}
        }
        if ($ExtraName) { $entry=$zip.CreateEntry($ExtraName);$stream=$entry.Open();try{$stream.WriteByte(1)}finally{$stream.Dispose()} }
    } finally {$zip.Dispose()}
}
function Alter-Xml([string]$Name,[scriptblock]$Change) {
    $data=Read-TestZip $testBundle
    $xml=$testUtf8.GetString($data['veterans.xml']); $xml=& $Change $xml
    $data['veterans.xml']=$testUtf8.GetBytes($xml)
    $manifest=ConvertFrom-Json $testUtf8.GetString($data['manifest.json'])
    foreach($record in $manifest.Files){if($record.Name -ceq 'veterans.xml'){$record.Bytes=$data['veterans.xml'].Length;$record.Sha256=Test-Hash $data['veterans.xml']}}
    $data['manifest.json']=$testUtf8.GetBytes(($manifest|ConvertTo-Json -Depth 6))
    $path=Fixture ($Name+'.zip');Write-TestZip $path $data;return $path
}
function New-Destination([string]$Name) {
    $profile=Fixture ($Name+'\DNOPersistentData\TestProfile')
    $game=Fixture ($Name+'\Game')
    [IO.Directory]::CreateDirectory($profile)|Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $game 'BepInEx\config'))|Out-Null
    return @{Mode='Import';BundlePath=$testBundle;GamePath=$game;ProfilePath=$profile;SaveName='ImportedVeterans'}
}

[IO.Directory]::CreateDirectory($testFixture)|Out-Null
$testGame=Fixture 'SourceGame'
$testProfile=Fixture ('source\DNOPersistentData\Profil'+[char]0x00E9)
$testSave=Join-Path $testProfile ('Partida'+[char]0x00F1)
$testSidecars=Join-Path $testGame 'BepInEx\config\VeteranSoldiers\saves'
$testSidecar=Join-Path $testSidecars ((Path-Hash $testSave)+'.xml')
$testBundle=Fixture 'export.veterans.zip'
Write-Text $testSave 'OPAQUE GAME SAVE FIXTURE - NEVER LOAD IN GAME'
Write-Text ($testSave+'.dat') 'OPAQUE HEADER FIXTURE'
$saveHash=(Get-FileHash -LiteralPath $testSave).Hash.ToLowerInvariant()
$pathHash=Path-Hash $testSave
$testXml='<VeteranSoldiers schema="2" saveSha256="'+$saveHash+'" pathSha256="'+$pathHash+'"><Unit referenceId="2318" objectId="68" xp="931" healthFactor="1.2" health="23.875" vanillaMaxHealth="20" specialization="2" /><Unit referenceId="2319" objectId="-10" xp="0" healthFactor="1" health="55" vanillaMaxHealth="55" specialization="0" /></VeteranSoldiers>'
Write-Text $testSidecar $testXml
$initialHashes=@{};foreach($path in @($testSave,($testSave+'.dat'),$testSidecar)){$initialHashes[$path]=(Get-FileHash -LiteralPath $path).Hash}
$export=& $testTool -Mode Export -BundlePath $testBundle -GamePath $testGame -SavePath $testSave
Assert-Test ($export.Status -eq 'Exported' -and $export.VeteranRecords -eq 2 -and $export.SidecarSchema -eq 2) 'Schema 2 export failed.'
$testData=Read-TestZip $testBundle
Assert-Test ($testData.Count -eq 4 -and $testUtf8.GetString($testData['manifest.json']) -notmatch '(?i)[a-z]:\\|Profil|Partida') 'Bundle exposes source path/name or extra entries.'
Pass 'Export schema 2 with opaque save/header and no plaintext source path/name'
$inspect=& $testTool -Mode Inspect -BundlePath $testBundle
Assert-Test ($inspect.Status -eq 'Verified' -and $inspect.VeteranRecords -eq 2 -and -not $inspect.AuthenticityVerified) 'Inspect failed.'
Pass 'Inspect verifies integrity without claiming authenticity'
$corrupt=Fixture 'not-a-zip.zip';Write-Text $corrupt 'not a ZIP archive'
Refuses {& $testTool -Mode Inspect -BundlePath $corrupt} 'Malformed ZIP rejected'
$exclusive=[IO.FileStream]::new($corrupt,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$exclusive.Dispose()
Pass 'Malformed ZIP construction releases its file handle immediately'
$destination=New-Destination 'good'
$beforeFiles=@(Get-ChildItem -LiteralPath (Fixture 'good') -File -Recurse).Count
$preview=& $testTool @destination
Assert-Test ($preview.Status -eq 'Preview' -and $preview.Writes -eq 0 -and @(Get-ChildItem -LiteralPath (Fixture 'good') -File -Recurse).Count -eq $beforeFiles) 'Preview wrote files.'
Pass 'Import preview has zero writes'
$import=& $testTool @destination -Apply
Assert-Test ($import.Status -eq 'Imported' -and $import.CreatedFiles -eq 3) 'Import failed.'
Assert-Test ((Get-FileHash -LiteralPath $import.SavePath).Hash -ceq $initialHashes[$testSave] -and (Get-FileHash -LiteralPath ($import.SavePath+'.dat')).Hash -ceq $initialHashes[$testSave+'.dat']) 'Opaque game bytes changed.'
$importedXml=[xml]([IO.File]::ReadAllText($import.SidecarPath))
Assert-Test ($importedXml.VeteranSoldiers.pathSha256 -ceq (Path-Hash $import.SavePath) -and $importedXml.VeteranSoldiers.saveSha256 -ceq $saveHash -and $importedXml.VeteranSoldiers.Unit[0].specialization -ceq '2' -and $importedXml.VeteranSoldiers.Unit[0].referenceId -ceq '2318' -and $importedXml.VeteranSoldiers.Unit[0].xp -ceq '931') 'Rebinding changed unit identity/progress.'
Pass 'Import rebinds only path identity and preserves exact save/header and specialization'
Refuses {& $testTool @destination -Apply} 'Existing destinations never overwritten' 'already exists'
Refuses {& $testTool -Mode Export -BundlePath $testBundle -GamePath $testGame -SavePath $testSave} 'Existing export bundle never overwritten' 'already exists'
$schema1=Alter-Xml 'schema1' {param($xml) $xml.Replace('schema="2"','schema="1"').Replace(' specialization="2"','').Replace(' specialization="0"','')}
$legacy=New-Destination 'legacy';$legacy.BundlePath=$schema1
$legacyResult=& $testTool @legacy -Apply
Assert-Test ($legacyResult.SidecarSchema -eq 1) 'Legacy schema not preserved.'
Pass 'Legacy schema 1 remains schema 1 with implicit no specialization'
foreach($case in @(
    @{Name='Wrong save fingerprint';Change={param($x)$x.Replace($saveHash,('a'*64))};Pattern='exact save'},
    @{Name='Wrong path identity';Change={param($x)$x.Replace($pathHash,('b'*64))};Pattern='source identity'},
    @{Name='Unknown XML schema';Change={param($x)$x.Replace('schema="2"','schema="99"')};Pattern='schema'},
    @{Name='Duplicate unit reference';Change={param($x)$x.Replace('referenceId="2319"','referenceId="2318"')};Pattern='Duplicate'},
    @{Name='Negative XP';Change={param($x)$x.Replace('xp="931"','xp="-1"')};Pattern='experience'},
    @{Name='Nonfinite XP';Change={param($x)$x.Replace('xp="931"','xp="NaN"')};Pattern='experience'},
    @{Name='Invalid health factor';Change={param($x)$x.Replace('healthFactor="1.2"','healthFactor="0.8"')};Pattern='health'},
    @{Name='Overflow health maximum';Change={param($x)$x.Replace('vanillaMaxHealth="20"','vanillaMaxHealth="3.4E38"')};Pattern='health'},
    @{Name='Missing specialization in schema 2';Change={param($x)$x.Replace(' specialization="2"','')};Pattern='attributes'},
    @{Name='Unknown specialization';Change={param($x)$x.Replace('specialization="2"','specialization="4"')};Pattern='specialization'},
    @{Name='DTD entity expansion';Change={param($x)'<!DOCTYPE VeteranSoldiers [<!ENTITY payload "text">]>'+$x};Pattern='DTD'},
    @{Name='Nested XML content';Change={param($x)$x.Replace('specialization="2" />','specialization="2"><Injected /></Unit>')};Pattern='element'}
)){
    $bad=Alter-Xml ('bad-'+$testPasses.Count) $case.Change
    Refuses {& $testTool -Mode Inspect -BundlePath $bad} $case.Name $case.Pattern
}
$tampered=Read-TestZip $testBundle;$tampered['save.bin'][0]=0
$bad=Fixture 'tampered.zip';Write-TestZip $bad $tampered
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'Tampered payload rejected before XML' 'manifest'
foreach($name in @('../escape','/rooted','save.bin','SAVE.BIN','folder/file')){
    $bad=Fixture ('entry-'+$testPasses.Count+'.zip');Write-TestZip $bad $testData $name
    Refuses {& $testTool -Mode Inspect -BundlePath $bad} ('Extra/traversal/collision entry '+$testPasses.Count) 'four entries'
}
foreach($name in @('../escape','/rooted','save.dat','SAVE.BIN','folder/file','C:\absolute','save.bin:stream')){
    $bad=Fixture ('four-entry-'+$testPasses.Count+'.zip');Write-TestZip $bad $testData -ReplaceSaveName $name
    Refuses {& $testTool -Mode Inspect -BundlePath $bad} ('Unsafe name within four-entry archive '+$testPasses.Count) 'unsafe ZIP'
}
$bad=Fixture 'symlink.zip';Write-TestZip $bad $testData '' ([int](-1610612736))
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'ZIP symlink rejected' 'links'
$bad=Fixture 'reparse.zip';Write-TestZip $bad $testData '' 1024
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'ZIP reparse attribute rejected' 'links'
$bad=Fixture 'directory.zip';Write-TestZip $bad $testData '' 16
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'ZIP directory attribute rejected' 'directories'
$bomb=Read-TestZip $testBundle;$bomb['save.bin']=[byte[]]::new(2097152)
$bad=Fixture 'bomb.zip';Write-TestZip $bad $bomb -Compressed
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'ZIP expansion bomb bounded before extraction' 'expansion'
$large=Read-TestZip $testBundle;$large['save.dat']=[byte[]]::new(16777217)
$bad=Fixture 'large.zip';Write-TestZip $bad $large -Compressed
Refuses {& $testTool -Mode Inspect -BundlePath $bad} 'Oversized header bounded before extraction' 'size limit'
$missingDatSave=Join-Path $testProfile 'NoHeader';Write-Text $missingDatSave 'save'
Refuses {& $testTool -Mode Export -BundlePath (Fixture 'noheader.zip') -GamePath $testGame -SavePath $missingDatSave} 'Missing .dat prevents incomplete export' 'save.dat'
Refuses {& $testTool -Mode Export -BundlePath (Fixture 'header-selected.zip') -GamePath $testGame -SavePath ($testSave+'.dat')} 'Header cannot be selected as the game save' 'extensionless'
$locked=[IO.FileStream]::new($testSave,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try {Refuses {& $testTool -Mode Export -BundlePath (Fixture 'locked.zip') -GamePath $testGame -SavePath $testSave} 'Source lock prevents inconsistent export'} finally {$locked.Dispose()}
Assert-Test (-not (Test-Path -LiteralPath (Fixture 'locked.zip'))) 'Locked export left a bundle.'
foreach($name in @('../escape','CON','wrong.dat','edge ','name:stream')){
    $badDestination=New-Destination ('name'+$testPasses.Count);$badDestination.SaveName=$name
    Refuses {& $testTool @badDestination -Apply} ('Unsafe save name '+$testPasses.Count) 'new save name'
}
$badDestination=New-Destination 'rootprofile';$badDestination.ProfilePath=Split-Path -Parent $badDestination.ProfilePath
Refuses {& $testTool @badDestination -Apply} 'Profile root cannot be used as a profile' 'one profile'
$linkDestination=New-Destination 'junction'
$real=Fixture 'junction-target';[IO.Directory]::CreateDirectory($real)|Out-Null
$link=Join-Path $linkDestination.GamePath 'BepInEx\config\VeteranSoldiers'
New-Item -ItemType Junction -Path $link -Target $real | Out-Null
Refuses {& $testTool @linkDestination -Apply} 'Destination junction rejected' 'Reparse'

# Inject a late collision at the actual commit statement, without a production test backdoor.
# Two files will have committed before the final save move fails; foreign content must survive.
$rollback=New-Destination 'rollback'
$global:saveTransferCollisionPath=Join-Path $rollback.ProfilePath $rollback.SaveName
$line=(Select-String -LiteralPath $testTool -SimpleMatch '[IO.File]::Move($record.Path,$targets[$name])').LineNumber
$breakpoint=Set-PSBreakpoint -Script $testTool -Line $line -Action {
    if (-not [IO.File]::Exists($global:saveTransferCollisionPath)) { [IO.File]::WriteAllText($global:saveTransferCollisionPath,'foreign collision') }
}
try { Refuses {& $testTool @rollback -Apply} 'Partial commit rollback preserves foreign collision' }
finally { Remove-PSBreakpoint -Breakpoint $breakpoint; Remove-Variable saveTransferCollisionPath -Scope Global }
$remaining=@(Get-ChildItem -LiteralPath (Fixture 'rollback') -File -Recurse)
Assert-Test ($remaining.Count -eq 1 -and [IO.File]::ReadAllText($remaining[0].FullName) -ceq 'foreign collision') 'Rollback left own files or removed foreign file.'
Pass 'Rollback removes only its own committed and staged files'

$changedRollback=New-Destination 'changed-rollback'
$global:saveTransferCollisionPath=Join-Path $changedRollback.ProfilePath $changedRollback.SaveName
$global:saveTransferChangedPath=Join-Path $changedRollback.GamePath ('BepInEx\config\VeteranSoldiers\saves\'+(Path-Hash $global:saveTransferCollisionPath)+'.xml')
$breakpoint=Set-PSBreakpoint -Script $testTool -Line $line -Action {
    if (-not [IO.File]::Exists($global:saveTransferCollisionPath)) {[IO.File]::WriteAllText($global:saveTransferCollisionPath,'foreign collision')}
    if ([IO.File]::Exists($global:saveTransferChangedPath)) {[IO.File]::WriteAllText($global:saveTransferChangedPath,'external change')}
}
try { Refuses {& $testTool @changedRollback -Apply -WarningAction SilentlyContinue} 'Rollback preserves externally changed created file' }
finally {Remove-PSBreakpoint -Breakpoint $breakpoint;Remove-Variable saveTransferCollisionPath,saveTransferChangedPath -Scope Global}
$remaining=@(Get-ChildItem -LiteralPath (Fixture 'changed-rollback') -File -Recurse)
Assert-Test ($remaining.Count -eq 2 -and @($remaining | Where-Object {[IO.File]::ReadAllText($_.FullName) -ceq 'external change'}).Count -eq 1) 'Rollback deleted externally changed content.'
Pass 'Changed owned artifact retained for recovery'
foreach($path in $initialHashes.Keys){Assert-Test ((Get-FileHash -LiteralPath $path).Hash -ceq $initialHashes[$path]) 'Source fixture changed.'}
Pass 'Source save, header and sidecar hashes remain unchanged'

# Exercise real Windows PowerShell 5.1 -File argument binding, not just in-process invocation.
$cli=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $testTool -Mode Inspect -BundlePath $testBundle 2>&1
Assert-Test ($LASTEXITCODE -eq 0 -and ($cli -join ' ') -match 'Verified') 'Windows PowerShell CLI invocation failed.'
Pass 'Windows PowerShell 5.1 CLI -File entry point'

# Schema 3 is preserved as a complete campaign, with exact building references and bounded histories.
$chronicle='<Chronicle identity="11111111111111111111111111111111" name="Aldric" favorite="1" damage="300" combat="3" kills="1" powerful="0" structures="0" level="2" objectId="68" medals="1"><Event kind="1" value="0" level="0" /><Event kind="3" value="1" level="0" /></Chronicle>'
$fallen='<Fallen identity="22222222222222222222222222222222" name="Mara" favorite="0" damage="0" combat="0" kills="0" powerful="0" structures="0" level="0" objectId="68" medals="0"><Event kind="4" value="0" level="0" /></Fallen>'
$campaign='<Campaign damage="300" kills="1" promotions="2" deaths="1">'+$fallen+'<Hall active="1" pending="2" progress="31.5" unlocked="3" combatXp="500"><Building referenceId="5000" baseObjectId="10" /></Hall></Campaign>'
$schema3=Alter-Xml 'schema3' {param($x)$x.Replace('schema="2"','schema="3"').Replace('specialization="2" />',('specialization="2">'+$chronicle+'</Unit>')).Replace('</VeteranSoldiers>',($campaign+'</VeteranSoldiers>'))}
$schema3Inspect=& $testTool -Mode Inspect -BundlePath $schema3
Assert-Test ($schema3Inspect.SidecarSchema -eq 3 -and $schema3Inspect.VeteranRecords -eq 2) 'Schema 3 inspection failed.'
$target3=New-Destination 'campaign3';$target3.BundlePath=$schema3
$import3=& $testTool @target3 -Apply
$xml3=[xml]([IO.File]::ReadAllText($import3.SidecarPath))
Assert-Test ($xml3.VeteranSoldiers.Campaign.Hall.Building.referenceId -ceq '5000' -and $xml3.VeteranSoldiers.Campaign.Hall.progress -ceq '31.5' -and $xml3.VeteranSoldiers.Unit[0].Chronicle.name -ceq 'Aldric' -and $xml3.VeteranSoldiers.Campaign.Fallen.name -ceq 'Mara') 'Schema 3 transfer lost campaign data.'
Pass 'Schema 3 transfer preserves named recruits, favorites, memorial, doctrines and exact Hall references'
$cli3=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $testTool -Mode Inspect -BundlePath $schema3 2>&1
Assert-Test ($LASTEXITCODE -eq 0 -and ($cli3 -join ' ') -match 'Verified') 'Schema 3 Windows PowerShell CLI failed.'
Pass 'Schema 3 validation works in Windows PowerShell 5.1'
$originalBundle=$testBundle;$testBundle=$schema3
foreach($case in @(
    @{Name='Duplicate living and fallen identity';Change={param($x)$x.Replace('22222222222222222222222222222222','11111111111111111111111111111111')};Pattern='identity'},
    @{Name='Invalid doctrine progress';Change={param($x)$x.Replace('progress="31.5"','progress="NaN"')};Pattern='progress'},
    @{Name='Unknown doctrine';Change={param($x)$x.Replace('active="1"','active="4"')};Pattern='active'},
    @{Name='Locked active doctrine';Change={param($x)$x.Replace('unlocked="3"','unlocked="0"')};Pattern='unlock'},
    @{Name='Missing pending doctrine';Change={param($x)$x.Replace('pending="2"','pending="0"')};Pattern='research'},
    @{Name='Unearned medals';Change={param($x)$x.Replace('damage="300"','damage="0"')};Pattern='evidence'},
    @{Name='Missing Hall state';Change={param($x)[regex]::Replace($x,'<Hall.*?</Hall>','')};Pattern='Hall'},
    @{Name='Markup nickname';Change={param($x)$x.Replace('name="Aldric"','name="&lt;red&gt;"')};Pattern='name'},
    @{Name='Missing observed death';Change={param($x)$x.Replace('kind="4"','kind="1"')};Pattern='death'},
    @{Name='Duplicate building reference';Change={param($x)$x.Replace('<Building referenceId="5000" baseObjectId="10" />','<Building referenceId="5000" baseObjectId="10" /><Building referenceId="5000" baseObjectId="10" />')};Pattern='reference'},
    @{Name='Oversized history';Change={param($x)$x.Replace('<Event kind="1" value="0" level="0" />',('<Event kind="1" value="0" level="0" />'*13))};Pattern='history'}
)) {
    $bad=Alter-Xml ('schema3-bad-'+$testPasses.Count) $case.Change
    Refuses {& $testTool -Mode Inspect -BundlePath $bad} $case.Name $case.Pattern
}
$testBundle=$originalBundle
[pscustomobject]@{Passed=$true;Checks=$testPasses.Count;Names=@($testPasses);Fixture=$testFixture;Scope='Synthetic opaque fixtures only; no real save or game process was opened.'}|ConvertTo-Json -Depth 4
