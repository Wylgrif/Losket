<#
.SYNOPSIS
    Compile Losket et le deploie dans l'install KSP de developpement.

.EXAMPLE
    .\Tools\build.ps1              # compile + deploie
    .\Tools\build.ps1 -Run         # compile + deploie + lance KSP
    .\Tools\build.ps1 -Deploy:$false   # compile seulement
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$KSPRoot = "",
    [switch]$Run,
    [bool]$Deploy = $true
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# --- dotnet ---------------------------------------------------------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "dotnet introuvable. Installe le SDK .NET 8." }

# --- resolution de KSPRoot ------------------------------------------------
if (-not $KSPRoot) {
    $local = Join-Path $repo "LocalSettings.props"
    if (Test-Path $local) {
        $m = Select-String -Path $local -Pattern '<KSPRoot>(.+?)</KSPRoot>' | Select-Object -First 1
        if ($m) { $KSPRoot = $m.Matches[0].Groups[1].Value }
    }
}
if (-not $KSPRoot) { $KSPRoot = "C:\KSPDev\KSP-1.12.5-Losket" }

# --- compilation ----------------------------------------------------------
Write-Host "==> Compilation ($Configuration)" -ForegroundColor Cyan
& $dotnet build (Join-Path $repo "Source\Losket\Losket.csproj") `
    -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "echec de la compilation" }

# --- deploiement ----------------------------------------------------------
if ($Deploy) {
    $srcGameData = Join-Path $repo "GameData\Losket"
    $dstGameData = Join-Path $KSPRoot "GameData\Losket"

    if (-not (Test-Path (Join-Path $KSPRoot "KSP_x64.exe"))) {
        throw "KSP introuvable dans '$KSPRoot' (pas de KSP_x64.exe)."
    }

    Write-Host "==> Deploiement vers $dstGameData" -ForegroundColor Cyan
    # /XF *.testbundle : preserve les bundles temoins deposes a la main dans
    # l'install de dev pour comparer notre sortie a celle d'un mod qui marche.
    robocopy $srcGameData $dstGameData /MIR /XF *.testbundle /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "echec du deploiement (robocopy $LASTEXITCODE)" }

    Get-ChildItem $dstGameData -Recurse -File |
        ForEach-Object { "    {0,10:N0}  {1}" -f $_.Length, $_.FullName.Substring($dstGameData.Length + 1) }
}

# --- lancement ------------------------------------------------------------
if ($Run) {
    $log = Join-Path $KSPRoot "KSP.log"
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
    Write-Host "==> Lancement de KSP" -ForegroundColor Cyan
    Start-Process -FilePath (Join-Path $KSPRoot "KSP_x64.exe") -WorkingDirectory $KSPRoot
    Write-Host "    log : $log"
}

Write-Host "OK" -ForegroundColor Green
exit 0
