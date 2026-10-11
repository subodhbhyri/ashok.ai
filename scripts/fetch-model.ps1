<#
.SYNOPSIS
  Downloads the all-MiniLM-L6-v2 ONNX model files into client/models/ and verifies their SHA-256.

.DESCRIPTION
  Hashes and the pinned Hugging Face revision live in scripts/model-manifest.json. A file that already
  exists with the right hash is left alone; a file with the wrong hash is deleted and the script fails.
  This is a dev-time and CI-time script. The app itself never downloads anything.

.PARAMETER Variant
  common = vocab only, fp32 / qint8 / quint8 = that model (plus vocab), all = everything.
#>
[CmdletBinding()]
param(
    [ValidateSet('fp32', 'qint8', 'quint8', 'all')]
    [string]$Variant = 'all',
    [string]$Destination = (Join-Path $PSScriptRoot '..\client\models')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress rendering makes Invoke-WebRequest very slow

$manifest = Get-Content (Join-Path $PSScriptRoot 'model-manifest.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

$wanted = $manifest.files | Where-Object {
    $_.variant -eq 'common' -or $Variant -eq 'all' -or $_.variant -eq $Variant
}

foreach ($file in $wanted) {
    $target = Join-Path $Destination $file.name

    if (Test-Path $target) {
        $actual = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -eq $file.sha256) {
            Write-Host "ok       $($file.name)"
            continue
        }
        Write-Host "stale    $($file.name) (hash differs), downloading again"
        Remove-Item $target -Force
    }

    $url = "https://huggingface.co/$($manifest.repository)/resolve/$($manifest.revision)/$($file.path)"
    Write-Host "download $($file.name) ($([math]::Round($file.size / 1MB, 1)) MB)"
    $partial = "$target.partial"
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing

    $actual = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $file.sha256) {
        Remove-Item $partial -Force
        throw "SHA-256 mismatch for $($file.name): expected $($file.sha256), got $actual"
    }
    Move-Item $partial $target -Force
}

Write-Host "Model files are in $Destination"
