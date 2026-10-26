# Builds the portable single-file Windows app into dist\StarRailAutoBattle.exe.
# Usage (from the repository root):  pwsh -File scripts/publish.ps1   (or: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

Push-Location $root
try {
    dotnet test StarRailAutoBattle.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "测试失败，已停止打包。" }

    if (Test-Path $dist) { Remove-Item (Join-Path $dist '*') -Recurse -Force }
    dotnet publish src/GameLauncher/GameLauncher.csproj -c Release -r win-x64 -p:DebugType=none -o $dist
    if ($LASTEXITCODE -ne 0) { throw "打包失败。" }

    $exe = Join-Path $dist 'StarRailAutoBattle.exe'
    Write-Host ""
    Write-Host "已生成：$exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
}
finally {
    Pop-Location
}
