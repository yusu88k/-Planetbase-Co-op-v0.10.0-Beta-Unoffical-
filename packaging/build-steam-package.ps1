# Builds the native Steam package from source: plugin DLL + texts + licenses + BepInEx base.
# Output: <coop-mod>\STEAM_PLANETBASE_COOP_<first 8 of DLL SHA-256>[.zip]
param(
    [string]$SteamGame = 'D:\SteamLibrary\steamapps\common\Planetbase',
    [string]$BaseFiles = 'STEAM_PLANETBASE_COOP_ADCEAA79'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$texts = Join-Path $PSScriptRoot 'steam'
$managed = Join-Path $SteamGame 'Planetbase_Data\Managed'
$out = Join-Path ([IO.Path]::GetTempPath()) ('pbcoop-build-' + [Guid]::NewGuid().ToString('N'))

dotnet build (Join-Path $root 'PlanetbaseCoop\PlanetbaseCoop.csproj') -c Release --no-incremental -o $out `
    "-p:PlanetbaseManagedPath=$managed" `
    "-p:BepInExCorePath=$SteamGame\BepInEx\core" `
    "-p:SteamworksManagedPath=$managed\com.rlabrecque.steamworks.net.dll" `
    -p:SteamworksReferenceName=com.rlabrecque.steamworks.net `
    -p:CopySteamworksManaged=false -p:BundleSteamRedistributable=false
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$plugin = Get-Content (Join-Path $root 'PlanetbaseCoop\Plugin.cs') -Raw
$version = [regex]::Match($plugin, '"Planetbase Co-op", "([^"]+)"').Groups[1].Value
$sync = Get-Content (Join-Path $root 'PlanetbaseCoop\SyncManager.cs') -Raw
$protocol = [regex]::Match($sync, 'ProtocolVersion = (\d+);').Groups[1].Value
$dll = Join-Path $out 'PlanetbaseCoop.dll'
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash
$name = 'STEAM_PLANETBASE_COOP_' + $hash.Substring(0, 8)
$package = Join-Path $root $name
if (Test-Path $package) { throw "$package already exists" }

New-Item -ItemType Directory $package | Out-Null
$base = Join-Path $root $BaseFiles
foreach ($item in 'BepInEx', '.doorstop_version', 'doorstop_config.ini', 'steam_appid.txt', 'winhttp.dll') {
    Copy-Item (Join-Path $base $item) $package -Recurse
}
Copy-Item $dll (Join-Path $package 'BepInEx\plugins\PlanetbaseCoop.dll') -Force
Copy-Item (Join-Path $texts 'THIRD_PARTY_NOTICES') $package -Recurse
foreach ($text in 'README_EN.txt', 'README_TR.txt') {
    $content = [IO.File]::ReadAllText((Join-Path $texts $text))
    $content = $content.Replace('@@VERSION@@', $version).Replace('@@PROTOCOL@@', $protocol).Replace('@@DLLHASH@@', $hash)
    [IO.File]::WriteAllText((Join-Path $package $text), $content, (New-Object Text.UTF8Encoding($false)))
}
Compress-Archive -Path (Join-Path $package '*') -DestinationPath "$package.zip"
Remove-Item $out -Recurse -Force
"version=$version protocol=$protocol"
"dll=$hash"
"zip=$((Get-FileHash "$package.zip" -Algorithm SHA256).Hash)"
"package=$package.zip"
