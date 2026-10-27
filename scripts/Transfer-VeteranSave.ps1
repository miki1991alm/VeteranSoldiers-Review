#requires -Version 5.1
<#
.SYNOPSIS
Exports or imports one game save, its .dat header and matching Veteran Soldiers sidecar.
.DESCRIPTION
Windows-only, offline transfer. Import previews destinations unless -Apply is supplied.
Never overwrites files. Close the game on both computers before transferring a trusted save.
See SAVE-TRANSFER.md for examples and limits. Bundles contain private game progress.
.EXAMPLE
./Transfer-VeteranSave.ps1 -Mode Export -GamePath 'D:\Games\DNO' -SavePath 'D:\Profiles\DNOPersistentData\Player\MySave' -BundlePath 'D:\Transfers\MySave.veterans.zip'
.EXAMPLE
./Transfer-VeteranSave.ps1 -Mode Import -GamePath 'D:\Games\DNO' -ProfilePath 'D:\Profiles\DNOPersistentData\Player' -SaveName 'ImportedVeterans' -BundlePath 'D:\Transfers\MySave.veterans.zip' -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Export','Inspect','Import')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [string]$GamePath,
    [string]$SavePath,
    [string]$ProfilePath,
    [string]$SaveName,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This tool supports Windows paths only.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$transferUtf8 = [Text.UTF8Encoding]::new($false, $true)
$transferInvariant = [Globalization.CultureInfo]::InvariantCulture
$transferLimits = @{ 'manifest.json'=16384L; 'save.bin'=268435456L; 'save.dat'=16777216L; 'veterans.xml'=33554432L }
$transferArchiveLimit = 335544320L
$transferOwned = [Collections.Generic.List[object]]::new()
$transferDirectories = [Collections.Generic.List[string]]::new()

function Assert-Transfer([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw [IO.InvalidDataException]::new($Message) }
}
function Get-FullSafePath([string]$Path) {
    Assert-Transfer (-not [string]::IsNullOrWhiteSpace($Path)) 'A required path is missing.'
    $full = [IO.Path]::GetFullPath($Path)
    Assert-Transfer ($full -match '^[A-Za-z]:\\' -and $full -notmatch ':.*:') 'Use a local drive path; device, UNC and alternate-stream paths are not supported.'
    foreach ($part in $full.Substring(3).Split('\')) {
        Assert-Transfer ($part -notmatch '[. ]$' -and $part -notmatch '^(?i:CON|PRN|AUX|NUL|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3])(?:\.|$)') 'Ambiguous trailing dots/spaces and Windows device names are not allowed in paths.'
    }
    $current = $full
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            Assert-Transfer (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse points, junctions and symbolic links are not allowed in transfer paths.'
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}
function Assert-ClosedGame {
    $running = @(Get-Process -Name 'Diplomacy is Not an Option' -ErrorAction SilentlyContinue)
    Assert-Transfer ($running.Count -eq 0) 'Close every Diplomacy is Not an Option instance normally, then retry. This tool never closes the game.'
}
function Get-Profile([string]$Path) {
    $full = Get-FullSafePath $Path
    Assert-Transfer ([IO.Directory]::Exists($full)) 'The target profile directory must already exist; create/select the profile in the game first.'
    Assert-Transfer ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($full.TrimEnd('\'))) -ceq 'DNOPersistentData') 'Select one profile folder directly inside DNOPersistentData, not the whole data directory.'
    return $full.TrimEnd('\')
}
function Get-SidecarDirectory([string]$Path) {
    $full = Get-FullSafePath $Path
    Assert-Transfer ([IO.Directory]::Exists((Join-Path $full 'BepInEx\config'))) 'GamePath must contain BepInEx\config. Run the installed mod once before transferring.'
    return (Get-FullSafePath (Join-Path $full 'BepInEx\config\VeteranSoldiers\saves'))
}
function Get-BytesHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Get-StreamHash([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $Stream.Position=0; $hash=[BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-','').ToLowerInvariant(); $Stream.Position=0; return $hash }
    finally { $sha.Dispose() }
}
function Get-PathHash([string]$Path) { return Get-BytesHash $transferUtf8.GetBytes([IO.Path]::GetFullPath($Path).ToUpperInvariant()) }
function Get-FileDigest([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Hash([string]$Hash) { Assert-Transfer ($Hash -cmatch '^[a-f0-9]{64}$') 'Invalid SHA-256 in transfer data.' }
function Assert-Properties($Object, [string[]]$Names) {
    Assert-Transfer ($null -ne $Object) 'Missing transfer metadata.'
    $actual = @($Object.PSObject.Properties.Name)
    Assert-Transfer ($actual.Count -eq $Names.Count) 'Unexpected transfer metadata properties.'
    foreach ($name in $actual) { Assert-Transfer ($Names -ccontains $name) 'Unknown transfer metadata property.' }
}
function Copy-Bounded([IO.Stream]$InputStream, [IO.Stream]$OutputStream, [long]$Expected) {
    $buffer = [byte[]]::new(65536)
    $copied = 0L
    while (($read = $InputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
        $copied += $read
        Assert-Transfer ($copied -le $Expected) 'Decompressed data exceeds its declared size.'
        $OutputStream.Write($buffer,0,$read)
    }
    Assert-Transfer ($copied -eq $Expected) 'Truncated transfer payload.'
}
function Assert-XmlAttributes($Element, [string[]]$Names) {
    Assert-Transfer ($Element -is [Xml.XmlElement] -and $Element.NamespaceURI -ceq '' -and $Element.Attributes.Count -eq $Names.Count) 'Unexpected chronicle attributes.'
    foreach ($name in $Names) { Assert-Transfer ($Element.HasAttribute($name)) 'Missing chronicle attribute.' }
}
function Read-Integer($Element,[string]$Name,[int]$Maximum=1000000000) {
    $value=[int]::Parse($Element.GetAttribute($Name),$transferInvariant)
    Assert-Transfer ($value -ge 0 -and $value -le $Maximum) ('Invalid chronicle '+$Name+'.')
    return $value
}
function Read-Number($Element,[string]$Name,[double]$Maximum=1e15) {
    $value=[double]::Parse($Element.GetAttribute($Name),$transferInvariant)
    Assert-Transfer (-not [double]::IsNaN($value) -and -not [double]::IsInfinity($value) -and $value -ge 0 -and $value -le $Maximum) ('Invalid chronicle '+$Name+'.')
    return $value
}
function Assert-Chronicle($Record,[bool]$Fallen,$Identities) {
    Assert-XmlAttributes $Record @('identity','name','favorite','damage','combat','kills','powerful','structures','level','objectId','medals')
    $id=$Record.GetAttribute('identity');$parsed=[Guid]::Empty
    Assert-Transfer ($id.Length -eq 32 -and [Guid]::TryParseExact($id,'N',[ref]$parsed) -and $parsed -ne [Guid]::Empty -and $parsed.ToString('N') -ceq $id -and $Identities.Add($id)) 'Duplicate or invalid chronicle identity.'
    $name=$Record.GetAttribute('name')
    Assert-Transfer ($name.Length -le 28 -and $name -ceq $name.Trim() -and $name -notmatch '  ') 'Invalid soldier name.'
    foreach ($char in $name.ToCharArray()) { Assert-Transfer ([char]::IsLetterOrDigit($char) -or $char -ceq ' ' -or $char -ceq '-' -or $char -ceq "'" -or $char -ceq '.') 'Invalid soldier name character.' }
    $null=Read-Integer $Record 'favorite' 1
    $damage=Read-Number $Record 'damage'
    $combat=Read-Integer $Record 'combat'
    $kills=Read-Integer $Record 'kills' $combat
    $powerful=Read-Integer $Record 'powerful' $kills;$structures=Read-Integer $Record 'structures' $kills
    $level=Read-Integer $Record 'level' 100;$null=[int]::Parse($Record.GetAttribute('objectId'),$transferInvariant)
    $medals=Read-Integer $Record 'medals' 63
    Assert-Transfer (-not ((($medals -band 1) -ne 0 -and $damage -lt 250) -or (($medals -band 2) -ne 0 -and $damage -lt 2000) -or (($medals -band 4) -ne 0 -and $kills -lt 25) -or (($medals -band 8) -ne 0 -and $powerful -lt 1) -or (($medals -band 16) -ne 0 -and $structures -lt 1) -or (($medals -band 32) -ne 0 -and $level -lt 2))) 'Medal lacks recorded evidence.'
    Assert-Transfer ($Record.ChildNodes.Count -le 12) 'Excessive soldier history.'
    $deaths=0;$last=0
    foreach($event in $Record.ChildNodes) {
        Assert-Transfer ($event.Name -ceq 'Event' -and $event.IsEmpty) 'Unexpected chronicle event.'
        Assert-XmlAttributes $event @('kind','value','level')
        $last=Read-Integer $event 'kind' 5;Assert-Transfer ($last -ge 1) 'Invalid chronicle event kind.'
        $eventValue=Read-Number $event 'value';$eventLevel=Read-Integer $event 'level' 100
        $invalid=($last -eq 1 -and ($eventValue -ne 0 -or $eventLevel -ne 0)) -or
            ($last -eq 2 -and ($eventValue -ne 0 -or $eventLevel -le 0 -or $eventLevel -gt $level)) -or
            ($last -eq 4 -and ($eventValue -ne 0 -or $eventLevel -ne $level)) -or
            ($last -eq 5 -and ($eventValue -lt 1 -or $eventValue -gt 3 -or $eventValue -ne [Math]::Floor($eventValue) -or $eventLevel -ne 0))
        if($last -eq 3) {$invalid=$eventLevel -ne 0 -or $eventValue -notin @(1,2,4,8,16,32) -or ($medals -band [int]$eventValue) -eq 0}
        Assert-Transfer (-not $invalid) 'Chronicle event contradicts soldier state.'
        if($last -eq 4){$deaths++}
    }
    Assert-Transfer ((-not $Fallen -and $deaths -eq 0) -or ($Fallen -and $deaths -eq 1 -and $last -eq 4)) 'Invalid memorial death evidence.'
}
function Assert-Campaign($Campaign,$Identities) {
    Assert-XmlAttributes $Campaign @('damage','kills','promotions','deaths')
    $null=Read-Number $Campaign 'damage';$null=Read-Integer $Campaign 'kills';$null=Read-Integer $Campaign 'promotions'
    $deaths=Read-Integer $Campaign 'deaths';$fallen=0;$halls=0
    foreach($entry in $Campaign.ChildNodes) {
        if($entry.LocalName -ceq 'Fallen') {
            $fallen++;Assert-Transfer ($fallen -le 256 -and $fallen -le $deaths) 'Excessive memorial records.'
            Assert-Chronicle $entry $true $Identities
        } elseif($entry.LocalName -ceq 'Hall') {
            $halls++;Assert-Transfer ($halls -eq 1) 'Duplicate Hall state.'
            Assert-XmlAttributes $entry @('active','pending','progress','unlocked','combatXp')
            $active=Read-Integer $entry 'active' 3;$pending=Read-Integer $entry 'pending' 3
            $unlocked=Read-Integer $entry 'unlocked' 7;$progress=Read-Number $entry 'progress' 60;$null=Read-Number $entry 'combatXp'
            Assert-Transfer (-not (($pending -eq 0 -and $progress -ne 0) -or ($active -gt 0 -and ($unlocked -band (1 -shl ($active-1))) -eq 0) -or ($pending -gt 0 -and ($unlocked -band (1 -shl ($pending-1))) -eq 0))) 'Hall doctrine contradicts unlock or research state.'
            Assert-Transfer ($entry.ChildNodes.Count -le 128) 'Excessive Hall buildings.'
            $references=[Collections.Generic.HashSet[int]]::new()
            foreach($building in $entry.ChildNodes) {
                Assert-Transfer ($building.Name -ceq 'Building' -and $building.IsEmpty) 'Unexpected Hall building.'
                Assert-XmlAttributes $building @('referenceId','baseObjectId')
                $reference=[int]::Parse($building.GetAttribute('referenceId'),$transferInvariant)
                $null=[int]::Parse($building.GetAttribute('baseObjectId'),$transferInvariant)
                Assert-Transfer ($reference -gt 0 -and $references.Add($reference)) 'Duplicate or invalid Hall save reference.'
            }
        } else {throw 'Unexpected campaign element.'}
    }
    Assert-Transfer ($halls -eq 1) 'Missing Hall state.'
}
function Read-Xml([byte[]]$Bytes, [string]$SaveHash, [string]$PathHash) {
    Assert-Transfer ($Bytes.LongLength -le $transferLimits['veterans.xml']) 'Sidecar exceeds the 32 MiB limit.'
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.IgnoreComments = $true
    $settings.IgnoreWhitespace = $true
    $settings.MaxCharactersInDocument = $transferLimits['veterans.xml']
    $memory = [IO.MemoryStream]::new($Bytes,$false)
    $reader = [Xml.XmlReader]::Create($memory,$settings)
    try {
        $doc = [Xml.XmlDocument]::new(); $doc.XmlResolver=$null; $doc.Load($reader)
        $root=$doc.DocumentElement
        Assert-Transfer ($root.Name -ceq 'VeteranSoldiers' -and $root.NamespaceURI -ceq '') 'Unexpected sidecar root.'
        Assert-Transfer ($root.Attributes.Count -eq 3 -and $root.HasAttribute('schema') -and $root.HasAttribute('saveSha256') -and $root.HasAttribute('pathSha256')) 'Unexpected sidecar header.'
        $schema = $root.GetAttribute('schema')
        Assert-Transfer ($schema -ceq '1' -or $schema -ceq '2' -or $schema -ceq '3') 'Unsupported sidecar schema; update this tool before importing.'
        Assert-Transfer ($root.GetAttribute('saveSha256') -ceq $SaveHash -and $root.GetAttribute('pathSha256') -ceq $PathHash) 'Veterancy does not match the exact save bytes and source identity.'
        $ids=[Collections.Generic.HashSet[int]]::new(); $count=0;$campaignCount=0
        $identities=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $attributes=@('referenceId','objectId','xp','healthFactor','health','vanillaMaxHealth')
        if ($schema -cne '1') { $attributes += 'specialization' }
        foreach ($unit in $root.ChildNodes) {
            if ($schema -ceq '3' -and $unit.Name -ceq 'Campaign') {
                $campaignCount++;Assert-Transfer ($campaignCount -eq 1) 'Duplicate campaign state.'
                Assert-Campaign $unit $identities
                continue
            }
            Assert-Transfer ($unit -is [Xml.XmlElement] -and $unit.Name -ceq 'Unit' -and $unit.NamespaceURI -ceq '' -and ($unit.IsEmpty -or $schema -ceq '3')) 'Unexpected sidecar element.'
            Assert-Transfer ($unit.Attributes.Count -eq $attributes.Count) 'Unexpected veteran record attributes.'
            foreach ($name in $attributes) { Assert-Transfer ($unit.HasAttribute($name)) 'Missing veteran record attribute.' }
            $ref=[int]::Parse($unit.GetAttribute('referenceId'),$transferInvariant)
            $null=[int]::Parse($unit.GetAttribute('objectId'),$transferInvariant)
            $xp=[double]::Parse($unit.GetAttribute('xp'),$transferInvariant)
            $factor=[single]::Parse($unit.GetAttribute('healthFactor'),$transferInvariant)
            $health=[single]::Parse($unit.GetAttribute('health'),$transferInvariant)
            $maximum=[single]::Parse($unit.GetAttribute('vanillaMaxHealth'),$transferInvariant)
            Assert-Transfer ($ref -gt 0 -and $ids.Add($ref) -and ++$count -le 100000) 'Duplicate, invalid or excessive saved-unit references.'
            Assert-Transfer (-not [double]::IsNaN($xp) -and -not [double]::IsInfinity($xp) -and $xp -ge 0) 'Invalid veteran experience.'
            foreach ($value in @($factor,$health,$maximum)) { Assert-Transfer (-not [single]::IsNaN($value) -and -not [single]::IsInfinity($value)) 'Non-finite veteran health.' }
            Assert-Transfer ($factor -ge 1 -and $health -gt 0 -and $maximum -gt 0 -and [double]$maximum*$factor -le [single]::MaxValue) 'Invalid veteran health factor or maximum.'
            if ($schema -cne '1') { $specialization=[int]::Parse($unit.GetAttribute('specialization'),$transferInvariant); Assert-Transfer ($specialization -ge 0 -and $specialization -le 3) 'Unsupported specialization.' }
            if (-not $unit.IsEmpty) {
                Assert-Transfer ($unit.ChildNodes.Count -eq 1 -and $unit.FirstChild.LocalName -ceq 'Chronicle') 'Unexpected soldier chronicle element.'
                Assert-Chronicle $unit.FirstChild $false $identities
            }
        }
        Assert-Transfer ($schema -cne '3' -or $campaignCount -eq 1) 'Missing schema 3 campaign state.'
        return [pscustomobject]@{Document=$doc;Records=$count;Schema=[int]$schema}
    }
    finally { $reader.Dispose(); $memory.Dispose() }
}
function Get-XmlBytes($Document) {
    $memory = [IO.MemoryStream]::new()
    $settings=[Xml.XmlWriterSettings]::new(); $settings.Encoding=$transferUtf8; $settings.Indent=$true
    $writer=[Xml.XmlWriter]::Create($memory,$settings)
    try { $Document.Save($writer); $writer.Flush(); return ,$memory.ToArray() }
    finally { $writer.Dispose(); $memory.Dispose() }
}
function Add-OwnedFile([string]$Path, [byte[]]$Bytes) {
    $full=Get-FullSafePath $Path
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $record=[pscustomobject]@{Path=$full;Hash=$null}; $transferOwned.Add($record)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    $record.Hash=Get-BytesHash $Bytes
    return $record
}
function Ensure-SidecarDirectory([string]$Path) {
    if ([IO.Directory]::Exists($Path)) { return }
    $parent=[IO.Path]::GetDirectoryName($Path)
    if (-not [IO.Directory]::Exists($parent)) { Ensure-SidecarDirectory $parent }
    $null=Get-FullSafePath $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $transferDirectories.Add($Path)
}
function Remove-OwnedArtifacts {
    # Only paths created by this invocation; changed files are preserved for manual recovery.
    foreach ($record in $transferOwned) {
        if ([IO.File]::Exists($record.Path)) {
            try {
                $null=Get-FullSafePath $record.Path
                if ($null -ne $record.Hash -and (Get-FileDigest $record.Path) -cne $record.Hash) { Write-Warning 'A transfer-created file changed after creation; preserved for manual recovery.'; continue }
                [IO.File]::Delete($record.Path)
            } catch { Write-Warning 'A transfer-created file could not be removed; inspect the selected destinations before retrying.' }
        }
    }
    for ($i=$transferDirectories.Count-1; $i -ge 0; $i--) {
        try { $null=Get-FullSafePath $transferDirectories[$i]; [IO.Directory]::Delete($transferDirectories[$i],$false) } catch { }
    }
}
function Read-Bundle([string]$Path) {
    Assert-Transfer ([IO.File]::Exists($Path) -and (Get-Item -LiteralPath $Path).Length -le $transferArchiveLimit) 'Bundle is absent or exceeds 320 MiB.'
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $zip=$null
    try {
        $zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Read,$false)
        Assert-Transfer ($zip.Entries.Count -eq 4) 'The bundle must contain exactly four entries.'
        $data=@{}; $total=0L
        foreach ($entry in $zip.Entries) {
            $name=$entry.FullName
            Assert-Transfer (@('manifest.json','save.bin','save.dat','veterans.xml') -ccontains $name -and -not $data.ContainsKey($name)) 'Unknown, duplicate, nested or unsafe ZIP entry.'
            $fileType=($entry.ExternalAttributes -shr 16) -band 0xF000
            Assert-Transfer (($fileType -eq 0 -or $fileType -eq 0x8000) -and ($entry.ExternalAttributes -band 0x410) -eq 0) 'ZIP links, directories and special files are refused.'
            Assert-Transfer ($entry.Length -gt 0 -and $entry.Length -le $transferLimits[$name]) 'ZIP entry exceeds its size limit or is empty.'
            Assert-Transfer ($entry.CompressedLength -gt 0 -and $entry.Length -le [Math]::Max(1048576L,$entry.CompressedLength*1000L)) 'Suspicious ZIP expansion ratio.'
            $total += $entry.Length
            Assert-Transfer ($total -le 318783488L) 'Combined decompressed size exceeds the transfer limit.'
            $input=$entry.Open(); $memory=[IO.MemoryStream]::new()
            try { Copy-Bounded $input $memory $entry.Length; $data[$name]=$memory.ToArray() }
            finally { $memory.Dispose(); $input.Dispose() }
        }
        $manifest=ConvertFrom-Json $transferUtf8.GetString($data['manifest.json'])
        Assert-Properties $manifest @('Format','Schema','TargetGameBuild','SourcePathSha256','Files')
        Assert-Transfer ($manifest.Format -ceq 'VeteranSoldiers.SaveTransfer' -and $manifest.Schema -eq 1 -and $manifest.TargetGameBuild -ceq '25478044') 'Unsupported transfer format or target game build.'
        Assert-Hash $manifest.SourcePathSha256
        Assert-Transfer (@($manifest.Files).Count -eq 3) 'Invalid payload manifest.'
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($record in $manifest.Files) {
            Assert-Properties $record @('Name','Bytes','Sha256')
            Assert-Transfer (@('save.bin','save.dat','veterans.xml') -ccontains $record.Name -and $seen.Add($record.Name)) 'Invalid or duplicate payload manifest entry.'
            Assert-Hash $record.Sha256
            Assert-Transfer ($record.Bytes -eq $data[$record.Name].LongLength -and (Get-BytesHash $data[$record.Name]) -ceq $record.Sha256) 'Payload bytes do not match the manifest.'
        }
        $xml=Read-Xml $data['veterans.xml'] (Get-BytesHash $data['save.bin']) $manifest.SourcePathSha256
        return [pscustomobject]@{Data=$data;Manifest=$manifest;Xml=$xml;BundleSha256=(Get-StreamHash $file)}
    }
    finally { if ($null -ne $zip) { $zip.Dispose() }; $file.Dispose() }
}

$bundle=Get-FullSafePath $BundlePath
Assert-Transfer (-not $Apply -or $Mode -eq 'Import') '-Apply is only used with Import.'
try {
    if ($Mode -eq 'Export') {
        Assert-ClosedGame
        $save=Get-FullSafePath $SavePath
        Assert-Transfer ([IO.Path]::GetExtension($save) -ceq '') 'Choose the extensionless game save, not its .dat header.'
        $null=Get-Profile ([IO.Path]::GetDirectoryName($save))
        $sidecars=Get-SidecarDirectory $GamePath
        $identity=Get-PathHash $save
        Assert-Transfer (-not (Test-Path -LiteralPath $bundle)) 'The bundle destination already exists; choose a new filename.'
        Assert-Transfer ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($bundle))) 'Create the bundle output directory first.'
        $paths=@{ 'save.bin'=$save; 'save.dat'=$save+'.dat'; 'veterans.xml'=(Join-Path $sidecars ($identity+'.xml')) }
        $streams=@{}; $zip=$null; $output=$null
        try {
            foreach ($name in @('save.bin','save.dat','veterans.xml')) {
                $path=Get-FullSafePath $paths[$name]
                Assert-Transfer ([IO.File]::Exists($path)) ('Required component is absent: '+$name+'. Save with Veteran Soldiers active and wait for the sidecar before exporting.')
                $streams[$name]=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                Assert-Transfer ($streams[$name].Length -gt 0 -and $streams[$name].Length -le $transferLimits[$name]) 'Source payload exceeds a transfer limit or is empty.'
            }
            $records=@(foreach ($name in @('save.bin','save.dat','veterans.xml')) { [ordered]@{Name=$name;Bytes=$streams[$name].Length;Sha256=(Get-StreamHash $streams[$name])} })
            $memory=[IO.MemoryStream]::new()
            try { Copy-Bounded $streams['veterans.xml'] $memory $streams['veterans.xml'].Length; $xml=Read-Xml $memory.ToArray() $records[0].Sha256 $identity; $streams['veterans.xml'].Position=0 }
            finally { $memory.Dispose() }
            # The opaque game binary is never deserialized: this is our supported target,
            # not a claim that the originating game's version was identified from its bytes.
            $manifest=[ordered]@{Format='VeteranSoldiers.SaveTransfer';Schema=1;TargetGameBuild='25478044';SourcePathSha256=$identity;Files=$records}
            $bytes=$transferUtf8.GetBytes(($manifest | ConvertTo-Json -Depth 6))
            $temporary=$bundle+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
            $owned=Add-OwnedFile $temporary ([byte[]]::new(0))
            $owned.Hash=$null
            $output=[IO.FileStream]::new($temporary,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $zip=[IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create,$true)
            foreach ($name in @('manifest.json','save.bin','save.dat','veterans.xml')) {
                # No compression avoids expansion bombs and gives deterministic bounded transfer size.
                $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::NoCompression)
                $destination=$entry.Open()
                try { if ($name -ceq 'manifest.json') { $destination.Write($bytes,0,$bytes.Length) } else { Copy-Bounded $streams[$name] $destination $streams[$name].Length } }
                finally { $destination.Dispose() }
            }
            $zip.Dispose(); $zip=$null; $output.Flush($true); $output.Dispose(); $output=$null
            $owned.Hash=Get-FileDigest $temporary
            $verified=Read-Bundle $temporary
            Assert-ClosedGame
            $null=Get-FullSafePath $bundle
            [IO.File]::Move($temporary,$bundle); $owned.Path=$bundle
            $transferOwned.Clear()
            [pscustomobject]@{Status='Exported';BundlePath=$bundle;BundleSha256=$verified.BundleSha256;VeteranRecords=$xml.Records;SidecarSchema=$xml.Schema;OriginalsChanged=$false}
        }
        finally { if ($null -ne $zip) {$zip.Dispose()}; if ($null -ne $output) {$output.Dispose()}; foreach ($stream in $streams.Values) {$stream.Dispose()} }
    } else {
        $loaded=Read-Bundle $bundle
        if ($Mode -eq 'Inspect') {
            [pscustomobject]@{Status='Verified';BundleSha256=$loaded.BundleSha256;TargetGameBuild=$loaded.Manifest.TargetGameBuild;VeteranRecords=$loaded.Xml.Records;SidecarSchema=$loaded.Xml.Schema;PayloadFiles=3;AuthenticityVerified=$false}
        } else {
            Assert-ClosedGame
            $profile=Get-Profile $ProfilePath
            $sidecars=Get-SidecarDirectory $GamePath
            Assert-Transfer (-not [string]::IsNullOrWhiteSpace($SaveName) -and $SaveName.Length -le 80 -and $SaveName -match '^[\p{L}\p{N}_-][\p{L}\p{N}_ -]*[\p{L}\p{N}_-]$|^[\p{L}\p{N}_-]$' -and $SaveName -notmatch '^(?i:CON|PRN|AUX|NUL|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3])$') 'Use a new save name of 1-80 letters, digits, spaces, underscores or hyphens, without extension or edge spaces.'
            $save=Get-FullSafePath (Join-Path $profile $SaveName)
            $identity=Get-PathHash $save
            $targets=@{ 'save.bin'=$save; 'save.dat'=$save+'.dat'; 'veterans.xml'=(Join-Path $sidecars ($identity+'.xml')) }
            foreach ($target in $targets.Values) { $null=Get-FullSafePath $target; Assert-Transfer (-not (Test-Path -LiteralPath $target)) 'A destination save, header or sidecar already exists. Choose a different save name; nothing is overwritten.' }
            $loaded.Xml.Document.DocumentElement.SetAttribute('pathSha256',$identity)
            $loaded.Data['veterans.xml']=Get-XmlBytes $loaded.Xml.Document
            $rebound=Read-Xml $loaded.Data['veterans.xml'] (Get-BytesHash $loaded.Data['save.bin']) $identity
            if (-not $Apply) {
                [pscustomobject]@{Status='Preview';SavePath=$save;HeaderPath=$targets['save.dat'];SidecarPath=$targets['veterans.xml'];VeteranRecords=$rebound.Records;SidecarSchema=$rebound.Schema;Writes=0;NextStep='Repeat the same Import command with -Apply after reviewing these three new destinations.'}
            } else {
                Ensure-SidecarDirectory $sidecars
                $pending=@{}
                foreach ($name in @('veterans.xml','save.dat','save.bin')) { $pending[$name]=Add-OwnedFile ($targets[$name]+'.'+[Guid]::NewGuid().ToString('N')+'.tmp') $loaded.Data[$name] }
                Assert-ClosedGame
                foreach ($name in @('veterans.xml','save.dat','save.bin')) {
                    $record=$pending[$name]; $null=Get-FullSafePath $targets[$name]
                    [IO.File]::Move($record.Path,$targets[$name]); $record.Path=$targets[$name]
                }
                foreach ($record in $transferOwned) { Assert-Transfer ((Get-FileDigest $record.Path) -ceq $record.Hash) 'Final transfer verification failed.' }
                Assert-ClosedGame
                $transferOwned.Clear(); $transferDirectories.Clear()
                [pscustomobject]@{Status='Imported';SavePath=$save;SidecarPath=$targets['veterans.xml'];VeteranRecords=$rebound.Records;SidecarSchema=$rebound.Schema;CreatedFiles=3;ExistingFilesChanged=$false;BundleSha256=$loaded.BundleSha256}
            }
        }
    }
} catch {
    Remove-OwnedArtifacts
    throw
}
