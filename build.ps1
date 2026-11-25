# Builds one mod, installs it in the Raft mods folder and publishes the pack to bestcode.pt/raft.
# Usage: .\build.ps1 -Dir src\calmfishing -Cs CalmFishing.cs -Rmod calm-fishing.rmod [-NoPublish]
param([string]$Dir, [string]$Cs, [string]$Rmod, [switch]$NoPublish)
if (-not [IO.Path]::IsPathRooted($Dir)) { $Dir = Join-Path $PSScriptRoot $Dir }
$ErrorActionPreference = 'Stop'
$sdk = (dotnet --list-sdks | Select-Object -Last 1) -replace '^(\S+) \[(.+)\]$', '$2\$1'
try { $mi = Get-Content (Join-Path $Dir 'modinfo.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { throw "modinfo.json invalido: $_" }
if (-not $mi -or -not $mi.name -or -not $mi.version) { throw 'modinfo.json vazio ou sem name/version' }
# a "Version" constant in the code must match modinfo.json (Mod Sync 2.0.1 shipped saying 2.0.0)
$code = Get-Content (Join-Path $Dir $Cs) -Raw -Encoding UTF8
$vm = [regex]::Match($code, '\bVersion\s*=\s*"([^"]+)"')
if ($vm.Success -and $vm.Groups[1].Value -ne $mi.version) { throw "versao no codigo ($($vm.Groups[1].Value)) diferente do modinfo.json ($($mi.version))" }
$rml = Join-Path $env:APPDATA 'RaftModLoader'
$managed = 'C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\Raft_Data\Managed'
$refs = @()
$refs += Join-Path $rml 'cache\publicizedAssemblies\Assembly-CSharp.dll'
$seen = @{}
Get-ChildItem $rml -Recurse -Include coremod.dll, HMLCoreLibrary.dll, 0Harmony.dll | Where-Object { $_.FullName -notmatch 'publicized' } | ForEach-Object {
    if (-not $seen.ContainsKey($_.Name)) { $seen[$_.Name] = 1; $refs += $_.FullName }
}
Get-ChildItem $managed -Filter *.dll | Where-Object { $_.Name -match '^(UnityEngine|Unity\.InputSystem|Unity\.TextMeshPro|mscorlib|System|netstandard|com\.rlabrecque)' } | ForEach-Object { $refs += $_.FullName }
$rargs = $refs | ForEach-Object { "-r:$_" }
$out = Join-Path $Dir 'out.dll'
$co = & dotnet "$sdk\Roslyn\bincore\csc.dll" -nologo -noconfig -nostdlib -t:library "-out:$out" @rargs (Join-Path $Dir $Cs) | Out-String
if ($LASTEXITCODE -ne 0) { throw ("compile failed:`n" + $co) }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$tmp = Join-Path $Dir $Rmod
if (Test-Path $tmp) { Remove-Item $tmp -Force }
$z = [System.IO.Compression.ZipFile]::Open($tmp, 'Create')
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $Dir 'modinfo.json'), 'modinfo.json')
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $Dir $Cs), $Cs)
$z.Dispose()
$dest = 'C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\mods'
Copy-Item $tmp (Join-Path $dest $Rmod) -Force
Get-Item (Join-Path $dest $Rmod) | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

if (-not $NoPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
