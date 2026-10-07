# Builds VoicePilot into dist\. Run: pwsh -File publish.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Get-Process VoicePilot -ErrorAction SilentlyContinue | Stop-Process   # files are locked while it runs
dotnet publish src\VoicePilot.App\VoicePilot.App.csproj -c Release -r win-x64 --self-contained false -o dist
dotnet publish src\VoicePilot.Cli\VoicePilot.Cli.csproj -c Release -r win-x64 --self-contained false -o dist
Write-Host "Ready: $PSScriptRoot\dist\VoicePilot.exe" -ForegroundColor Green
