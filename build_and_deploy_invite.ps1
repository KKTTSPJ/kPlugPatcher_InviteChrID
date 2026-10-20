# build_and_deploy_invite.ps1
# Powershell 7 compatible build + deploy for kPlugPatcher_InviteChrID
# KK / KKP の両構成をビルドし、それぞれ個別のDLLとしてデプロイする。
# ==========================================

$ErrorActionPreference = "Stop"

$ProjectFile = "$PSScriptRoot\kPlugPatcher_InviteChrID\kPlugPatcher_InviteChrID.csproj"
$DeployBase = "$PSScriptRoot\..\Deploy\kPlugPatcher_InviteChrID"

if (!(Test-Path $DeployBase)) {
    New-Item -ItemType Directory -Force -Path $DeployBase | Out-Null
}

Write-Host "`n>>> Starting Build <<<" -ForegroundColor Cyan

# 1. Build Release_KK (for Koikatu)
Write-Host "Building Release_KK..." -ForegroundColor Gray
dotnet build $ProjectFile --configuration Release_KK

if ($LASTEXITCODE -ne 0) {
    Write-Error "Release_KK Build failed!"
    exit 1
}

$KKSourcePath = "$PSScriptRoot\kPlugPatcher_InviteChrID\bin\Release_KK\net35\kPlugPatcher_InviteChrID.dll"
if (!(Test-Path $KKSourcePath)) {
    Write-Error "KK DLL not found: $KKSourcePath"
    exit 1
}
Copy-Item $KKSourcePath $DeployBase -Force
Write-Host "Deployed: kPlugPatcher_InviteChrID.dll" -ForegroundColor Green

# 2. Build Release_KKP (for Koikatsu Party)
Write-Host "Building Release_KKP..." -ForegroundColor Gray
dotnet build $ProjectFile --configuration Release_KKP

if ($LASTEXITCODE -ne 0) {
    Write-Error "Release_KKP Build failed!"
    exit 1
}

$KKPSourcePath = "$PSScriptRoot\kPlugPatcher_InviteChrID\bin\Release_KKP\net35\kPlugPatcher_InviteChrID_KKP.dll"
if (!(Test-Path $KKPSourcePath)) {
    Write-Error "KKP DLL not found: $KKPSourcePath"
    exit 1
}
Copy-Item $KKPSourcePath $DeployBase -Force
Write-Host "Deployed: kPlugPatcher_InviteChrID_KKP.dll" -ForegroundColor Green

Write-Host "`nAll Deployments completed successfully!" -ForegroundColor Magenta
