# Builds every mod in src\ whose code or modinfo.json differs from the .rmod installed in the Raft
# mods folder, then publishes the pack once (Mod Sync picks the changes up on every player's game).
# Usage: update            (builds what changed and publishes)
#        update -All       (rebuilds every mod)
#        update -NoPublish (builds and installs here only)
param([switch]$All, [switch]$NoPublish)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$mods = 'C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\mods'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function Read-Entry($zip, $name) {
    $e = $zip.GetEntry($name); if (-not $e) { return $null }
    $r = New-Object IO.StreamReader($e.Open()); try { return $r.ReadToEnd() } finally { $r.Dispose() }
}

# installed mods: modinfo name -> file, plus their contents
$installed = @{}
Get-ChildItem $mods -Filter *.rmod | ForEach-Object {
    try {
        $z = [IO.Compression.ZipFile]::OpenRead($_.FullName)
        try {
            $mi = Read-Entry $z 'modinfo.json'
            if ($mi) {
                $n = ($mi | ConvertFrom-Json).name
                $installed[$n] = @{ File = $_.Name; Zip = $_.FullName; Info = $mi }
            }
        } finally { $z.Dispose() }
    } catch { Write-Host "aviso: nao consegui ler $($_.Name)" -ForegroundColor Yellow }
}

$built = @(); $failed = @()
foreach ($d in Get-ChildItem (Join-Path $here 'src') -Directory) {
    $miPath = Join-Path $d.FullName 'modinfo.json'
    if (-not (Test-Path $miPath)) { continue }
    $cs = @(Get-ChildItem $d.FullName -Filter *.cs)
    if ($cs.Count -ne 1) { Write-Host "$($d.Name): precisa de exatamente 1 ficheiro .cs, ignorado" -ForegroundColor Yellow; continue }
    $cs = $cs[0]
    $miText = Get-Content $miPath -Raw -Encoding UTF8
    $mi = $miText | ConvertFrom-Json
    $inst = $installed[$mi.name]
    $rmod = if ($inst) { $inst.File } else { ($mi.name.ToLower() -replace '[^a-z0-9]+', '-').Trim('-') + '.rmod' }

    $changed = $All -or -not $inst
    if (-not $changed) {
        $z = [IO.Compression.ZipFile]::OpenRead($inst.Zip)
        try {
            $oldCs = Read-Entry $z $cs.Name
            $changed = ($oldCs -ne (Get-Content $cs.FullName -Raw -Encoding UTF8)) -or ($inst.Info -ne $miText)
        } finally { $z.Dispose() }
    }
    if (-not $changed) { continue }

    if ($inst) {
        $oldV = ($inst.Info | ConvertFrom-Json).version
        if ($oldV -eq $mi.version) { Write-Host "$($mi.name): mudou mas a versao continua $oldV (sobe a versao no codigo e no modinfo.json)" -ForegroundColor Yellow }
    }
    Write-Host "==> $($mi.name) $($mi.version) ($rmod)" -ForegroundColor Cyan
    try {
        & (Join-Path $here 'build.ps1') -Dir $d.FullName -Cs $cs.Name -Rmod $rmod -NoPublish | Out-Null
        $built += "$($mi.name) $($mi.version)"
    } catch {
        Write-Host $_ -ForegroundColor Red
        $failed += $mi.name
    } finally {
        Remove-Item (Join-Path $d.FullName 'out.dll'), (Join-Path $d.FullName $rmod) -ErrorAction SilentlyContinue
    }
}

if ($built.Count -eq 0 -and $failed.Count -eq 0) { Write-Host 'Nada mudou, nada para publicar.' -ForegroundColor Green; return }
if ($built.Count -gt 0) { Write-Host ("Compilados: " + ($built -join ', ')) -ForegroundColor Green }
if ($failed.Count -gt 0) { Write-Host ("FALHARAM: " + ($failed -join ', ') + " (nao publicados, corrige e corre update outra vez)") -ForegroundColor Red }
if ($built.Count -gt 0 -and -not $NoPublish) { & (Join-Path $here 'publish.ps1') }
