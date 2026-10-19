@echo off
rem ---------------------------------------------------------------------------
rem  Zipper.bat - fabrique GameData\LosketX.Y.Z.zip pour la release.
rem
rem  - X.Y.Z est lu dans GameData\Losket\Losket.version (champ VERSION).
rem  - Le zip a pour racine Losket\ et contient tout GameData\Losket, sauf
rem    Textures\Losket-Toolbarfullsise.png (inutile une fois le mod installe).
rem  - Le script se repere par rapport a son propre emplacement (Tools\), donc
rem    il fonctionne depuis n'importe quel clone du depot, quel que soit le
rem    dossier ou il se trouve, et quel que soit le dossier courant.
rem ---------------------------------------------------------------------------
setlocal
set "LOSKET_BAT=%~f0"
set "LOSKET_ROOT=%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:LOSKET_BAT); & ([scriptblock]::Create($s.Substring($s.IndexOf('#'+'PS#'))))"
set "CODE=%ERRORLEVEL%"
echo.
pause
exit /b %CODE%

#PS#
$ErrorActionPreference = 'Stop'
try {
    $root = (Resolve-Path -LiteralPath $env:LOSKET_ROOT).Path
    $src  = Join-Path $root 'GameData\Losket'
    $ver  = Join-Path $src 'Losket.version'

    if (-not (Test-Path -LiteralPath $ver)) {
        throw "Introuvable : $ver`n(ce script doit rester dans le dossier Tools du depot)"
    }

    # Version X.Y.Z depuis le champ VERSION (pas KSP_VERSION).
    $v = (Get-Content -LiteralPath $ver -Raw | ConvertFrom-Json).VERSION
    $version = '{0}.{1}.{2}' -f $v.MAJOR, $v.MINOR, $v.PATCH
    $zip = Join-Path $root "GameData\Losket$version.zip"

    if (-not (Test-Path -LiteralPath (Join-Path $src 'Plugins\Losket.dll'))) {
        Write-Warning 'Plugins\Losket.dll est absent (mod non compile) : le zip sera incomplet.'
    }
    if (-not (Get-ChildItem -Path (Join-Path $src 'Shaders') -Filter *.shaderbundle -ErrorAction SilentlyContinue)) {
        Write-Warning 'Aucun *.shaderbundle dans Shaders\ : le zip sera incomplet.'
    }

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Fichiers exclus, chemins relatifs a GameData\Losket (comparaison insensible a la casse).
    $exclus = @('Textures\Losket-Toolbarfullsise.png')

    $n = 0
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($src.Length).TrimStart('\')
            if ($exclus -contains $rel) {
                Write-Host "  (ignore) $rel"
                return
            }
            # Entrees en '/' (Compress-Archive de Windows PowerShell 5.1 ecrit des '\').
            $entry = 'Losket/' + ($rel -replace '\\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
            $n++
        }
    } finally {
        $archive.Dispose()
    }

    Write-Host ''
    Write-Host "OK : $zip"
    Write-Host "     version $version, $n fichiers"
    exit 0
} catch {
    Write-Host "ERREUR : $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
