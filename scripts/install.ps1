# FMO SAS 一键安装脚本（Windows）
# 用法: irm <url>/install.ps1 | iex
# 或:   $env:SAS_VERSION="v1.0.0"; irm <url>/install.ps1 | iex

param(
    [string]$BaseUrl = $env:SAS_BASE_URL ?? "https://cdn.example.com/sas",
    [string]$Version = $env:SAS_VERSION ?? "latest"
)

$ErrorActionPreference = "Stop"
$InstallDir = "$env:LOCALAPPDATA\SAS"
$Rid = "win-x64"

Write-Host "Platform: Windows x64" -ForegroundColor Cyan

$Url = "$BaseUrl/$Version/sas-$Rid.zip"
$Zip = "$env:TEMP\sas-install.zip"

Write-Host "Downloading $Url ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $Url -OutFile $Zip

Write-Host "Installing to $InstallDir ..." -ForegroundColor Cyan
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
Expand-Archive -Path $Zip -DestinationPath $InstallDir -Force
Remove-Item $Zip

Write-Host ""
Write-Host "═══ SAS installed ═══" -ForegroundColor Green
Write-Host "  Directory: $InstallDir"
Write-Host "  Double-click Sas.exe to start interactive setup."
Write-Host ""
