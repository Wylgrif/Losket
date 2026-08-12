<#
.SYNOPSIS
    Affiche les lignes de KSP.log qui concernent Losket (ou une autre recherche).

.EXAMPLE
    .\Tools\logtail.ps1                 # tout ce qui contient [Losket]
    .\Tools\logtail.ps1 -Follow         # suit le fichier en direct
    .\Tools\logtail.ps1 -Pattern Shader # autre motif
#>
[CmdletBinding()]
param(
    [string]$KSPRoot = "C:\KSPDev\KSP-1.12.5-Losket",
    [string]$Pattern = "\[Losket\]|Exception|NullReference",
    [switch]$Follow
)

$log = Join-Path $KSPRoot "KSP.log"
if (-not (Test-Path $log)) { throw "KSP.log introuvable dans $KSPRoot — lance le jeu au moins une fois." }

if ($Follow) {
    Get-Content $log -Wait -Tail 0 | Where-Object { $_ -match $Pattern }
} else {
    Select-String -Path $log -Pattern $Pattern | ForEach-Object { $_.Line }
}
