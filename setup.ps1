# NEWS DESK - one-time setup for a new Windows 10/11 computer.
# Installs what the app needs (with winget), builds it, and puts a NEWS DESK
# shortcut on the desktop. Safe to run again: anything already installed is skipped.
#
#   Right-click SETUP.bat > Run, or:  powershell -ExecutionPolicy Bypass -File setup.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Install-WithWinget($id, $name) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget is not available. Install 'App Installer' from the Microsoft Store, then run setup again."
    }
    Write-Host "Installing $name..."
    winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) {   # -1978335189 = already installed
        throw "Installing $name failed (winget exit code $LASTEXITCODE)."
    }
}

function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')
}

Step "1/5  .NET 8 SDK"
$hasSdk = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $hasSdk = (dotnet --list-sdks 2>$null) -match '^(8|9|10)\.'
}
if ($hasSdk) { Write-Host "Found." } else { Install-WithWinget 'Microsoft.DotNet.SDK.8' '.NET 8 SDK'; Refresh-Path }

Step "2/5  Microsoft Edge WebView2 Runtime (draws the projector output)"
$wv2 = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
) | Where-Object { Test-Path $_ }
if ($wv2) { Write-Host "Found." } else { Install-WithWinget 'Microsoft.EdgeWebView2Runtime' 'WebView2 Runtime' }

Step "3/5  FFmpeg (converts camera video: MTS, MOV, CR3 stills...)"
if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { Write-Host "Found." }
else { Install-WithWinget 'Gyan.FFmpeg' 'FFmpeg'; Refresh-Path }

Step "4/5  Building NEWS DESK"
dotnet build "$root\NewsDesk.App\NewsDesk.App.csproj" -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "The build failed - see the errors above." }

Step "5/5  Desktop shortcut"
$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $desktop 'NEWS DESK.lnk'))
$link.TargetPath = Join-Path $root 'LAUNCH_NEWS_DESK.bat'
$link.WorkingDirectory = $root
$link.IconLocation = (Join-Path $root 'NewsDesk.App\bin\Release\net8.0-windows\NewsDesk.App.exe') + ',0'
$link.WindowStyle = 7   # start minimised: only the app window shows
$link.Save()

Write-Host "`nDone. Open NEWS DESK from the desktop shortcut." -ForegroundColor Green
Write-Host "Moving a show from another computer? In the app: SETUP > MOVE THE SHOW > OPEN SHOW FILE."
