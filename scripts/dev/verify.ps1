[CmdletBinding()]
param(
    [switch]$IncludeAndroid
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$localDotNet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotNet) { $localDotNet } else { 'dotnet' }

Write-Host 'Building Windows Core and Desktop (Release)...'
& $dotnetCommand build (Join-Path $repoRoot 'PhoneUnlock.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }

Write-Host 'Running cryptographic self-tests...'
& $dotnetCommand run --project (Join-Path $repoRoot 'windows\PhoneUnlock.Core.Tests\PhoneUnlock.Core.Tests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core self-tests failed.' }

if ($IncludeAndroid) {
    $androidRoot = Join-Path $repoRoot 'android\PhoneUnlock'
    $localJdkRoot = Join-Path $repoRoot '.tools\jdk17'
    if (-not $env:JAVA_HOME -and (Test-Path -LiteralPath $localJdkRoot)) {
        $localJdk = Get-ChildItem -LiteralPath $localJdkRoot -Directory | Select-Object -First 1
        if ($null -ne $localJdk) {
            $env:JAVA_HOME = $localJdk.FullName
        }
    }

    if (-not $env:ANDROID_HOME) {
        $defaultSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
        if (Test-Path -LiteralPath $defaultSdk) {
            $env:ANDROID_HOME = $defaultSdk
        }
    }

    Write-Host 'Building Android debug APK...'
    Push-Location $androidRoot
    try {
        & '.\gradlew.bat' :app:assembleDebug --no-daemon
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    }
    finally {
        Pop-Location
    }
}

Write-Host 'Verification complete.'
