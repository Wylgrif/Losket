# Affiche les lignes de KSP.log qui concernent Losket (ou un autre motif).
#
#   .\Tools\logtail.ps1              toutes les lignes [Losket] et les erreurs
#   .\Tools\logtail.ps1 -Follow      suit le fichier en direct
#   .\Tools\logtail.ps1 -Pattern Shader
#
# Note : ce fichier est volontairement en ASCII pur. PowerShell 5.1 lit les .ps1
# sans BOM comme du Windows-1252, donc un caractere accentue ou un tiret cadratin
# casse l'analyse du script.

[CmdletBinding()]
param(
    [string]$KSPRoot = "C:\KSPDev\KSP-1.12.5-Losket",
    [string]$Pattern = "\[Losket\]|Exception|NullReference|AssetBundle",
    [switch]$Follow
)

# KSP ecrit deux journaux : KSP.log a la racine du jeu, et le Player.log d'Unity
# dans AppData\LocalLow. Le premier peut rester bloque sur une ancienne session
# (fichier verrouille, droits, lancement hors launcher) alors que le second suit.
# On prend systematiquement le plus recent des deux, en lisant le flux plutot que
# la date de l'entree de repertoire, qui n'est pas rafraichie tant que le jeu
# tient le fichier ouvert.
$candidates = @(
    (Join-Path $KSPRoot "KSP.log"),
    (Join-Path $env:USERPROFILE "AppData\LocalLow\Squad\Kerbal Space Program\Player.log")
) | Where-Object { Test-Path $_ }

if ($candidates.Count -eq 0) {
    throw "Aucun journal trouve. Lance le jeu au moins une fois."
}

$log = $candidates | Sort-Object { (Get-Item $_).LastWriteTime } | Select-Object -Last 1
Write-Host "--- source : $log ---" -ForegroundColor DarkGray

if ($Follow) {
    Get-Content $log -Wait -Tail 0 | Where-Object { $_ -match $Pattern }
} else {
    # Lecture en partage lecture/ecriture : Select-String echouerait ou lirait une
    # version perimee tant que KSP tient le fichier ouvert.
    $fs = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $sr = New-Object IO.StreamReader($fs)
    $content = $sr.ReadToEnd()
    $sr.Close(); $fs.Close()
    ($content -split "`r?`n") | Where-Object { $_ -match $Pattern }
}
