# SAS 多平台编译打包脚本
# 产出目录: bin/win-x64.zip, bin/linux-x64.tar.gz, bin/linux-arm64.tar.gz,
#           bin/osx-arm64.tar.gz, bin/osx-x64.tar.gz

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "src"
$bin = Join-Path $root "bin"

Write-Host "=== Publishing SAS ===" -ForegroundColor Cyan

$targets = @(
    @{ Rid = "win-x64";       Name = "Windows x64";          Ext = "zip" }
    @{ Rid = "linux-x64";     Name = "Linux x64";            Ext = "tar.gz" }
    @{ Rid = "linux-arm64";   Name = "Linux ARM64";          Ext = "tar.gz" }
    @{ Rid = "osx-arm64";     Name = "macOS Apple Silicon";  Ext = "tar.gz" }
    @{ Rid = "osx-x64";       Name = "macOS Intel";          Ext = "tar.gz" }
)

foreach ($t in $targets) {
    $outDir = Join-Path $bin $t.Rid
    Write-Host "  Publishing $($t.Name)..." -ForegroundColor Yellow

    dotnet publish "$src\Sas.csproj" `
        -c $Configuration `
        -r $t.Rid `
        --self-contained `
        -p:PublishSingleFile=true `
        -o $outDir

    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($t.Rid)" }

    if ($t.Ext -eq "tar.gz") {
        # tar.gz for Linux platforms
        $archive = "$bin\$($t.Rid).tar.gz"
        $tar = "$bin\$($t.Rid).tar"
        Push-Location $outDir
        & tar -cf $tar *
        Pop-Location
        & gzip -f $tar
        if (Test-Path "$tar.gz") { Move-Item "$tar.gz" $archive -Force }
        Write-Host "    -> $archive" -ForegroundColor Green
    }
    else {
        # zip for Windows
        $archive = "$bin\$($t.Rid).zip"
        Compress-Archive -Path "$outDir\*" -DestinationPath $archive -Force
        Write-Host "    -> $archive" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Get-ChildItem $bin -File | ForEach-Object {
    $sizeKB = [math]::Round($_.Length / 1KB, 1)
    Write-Host "  $($_.Name) ($sizeKB KB)"
}
