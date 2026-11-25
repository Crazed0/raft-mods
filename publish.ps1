# Publishes every .rmod in the Raft mods folder to https://bestcode.pt/raft/
#   raft-mods.zip  (all mods, for a first install)
#   mods/<name>    (single files, used by the Mod Sync mod)
#   manifest.json  (name, size, sha256 of each mod)
#   index.html     (fixed loader) + app.html (download page, loaded as app.html?v=<hash>)
# Usage: .\publish.ps1
$ErrorActionPreference = 'Stop'
$mods = 'C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\mods'
$here = $PSScriptRoot
$out = Join-Path $here 'publish-out'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\mods" | Out-Null

# this PC is the source: Mod Sync must never overwrite it
$marker = Join-Path $mods '.modsync-source'
if (-not (Test-Path $marker)) { Set-Content $marker 'source PC: Mod Sync does not touch this folder' -Encoding ASCII }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# reads modinfo.json from inside a .rmod (title, version, author, description); empty when missing
function Read-ModInfo([string]$path) {
    try {
        $z = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $e = $z.Entries | Where-Object { $_.FullName -ieq 'modinfo.json' } | Select-Object -First 1
            if (-not $e) { return $null }
            $r = New-Object IO.StreamReader($e.Open(), [Text.Encoding]::UTF8)
            try { return $r.ReadToEnd() | ConvertFrom-Json } finally { $r.Dispose() }
        } finally { $z.Dispose() }
    } catch { return $null }
}

$files = Get-ChildItem $mods -Filter *.rmod | Sort-Object Name
$entries = @()
foreach ($f in $files) {
    Copy-Item $f.FullName "$out\mods\$($f.Name)"
    $info = Read-ModInfo $f.FullName
    $entries += [ordered]@{
        name = $f.Name; size = $f.Length; sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
        title = $(if ($info -and $info.name) { [string]$info.name } else { '' })
        version = $(if ($info -and $info.version) { [string]$info.version } else { '' })
        author = $(if ($info -and $info.author) { [string]$info.author } else { '' })
        description = $(if ($info -and $info.description) { [string]$info.description } else { '' })
        modified = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
    }
}
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
# page version: hash of site\app.html, so index.html loads app.html?v=<hash> (busts any CDN cache)
$siteVer = (Get-FileHash (Join-Path $here 'site\app.html') -Algorithm SHA256).Hash.Substring(0, 12).ToLower()

$zip = "$out\raft-mods.zip"
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in $files) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, "mods/$($f.Name)") }
$z.Dispose()
# Cloudflare caches .zip by URL, so the page links a copy whose name changes with its content
$zipName = 'raft-mods-' + (Get-FileHash $zip -Algorithm SHA256).Hash.Substring(0, 12).ToLower() + '.zip'
Copy-Item $zip (Join-Path $out $zipName)

$manifest = [ordered]@{ updated = $stamp; site = $siteVer; zip = $zipName; files = $entries } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText("$out\manifest.json", $manifest, (New-Object Text.UTF8Encoding $false))

# site\index.html is a fixed loader; the real page is site\app.html
Get-ChildItem (Join-Path $here 'site') -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $out $_.Name) }

# upload: send a tar, swap in place
$tar = Join-Path $here 'publish-out.tar'
if (Test-Path $tar) { Remove-Item $tar -Force }
tar -cf $tar -C $out .
scp -q $tar web:/tmp/raft-publish.tar
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
ssh web "set -e; d=/var/www/raft; n=`$d.new; sudo -n rm -rf `$n; sudo -n mkdir -p `$n; sudo -n tar -xf /tmp/raft-publish.tar -C `$n --no-same-owner; sudo -n chown -R root:www-data `$n; sudo -n chmod -R u=rwX,g=rX,o= `$n; sudo -n rm -rf `$d.old; sudo -n mv `$d `$d.old; sudo -n mv `$n `$d; sudo -n rm -rf `$d.old; rm -f /tmp/raft-publish.tar; echo ok"
if ($LASTEXITCODE -ne 0) { throw 'remote swap failed' }
Remove-Item $tar -Force
Write-Host "Publicado $($files.Count) mods ($stamp) em https://bestcode.pt/raft/"
