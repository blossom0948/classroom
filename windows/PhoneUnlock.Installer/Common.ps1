Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:PhoneUnlockProviderGuid = '{8C12D44B-04D3-41D4-980B-80DF3D8DD324}'
$script:PhoneUnlockServiceName = 'PhoneUnlockService'
$script:PhoneUnlockFirewallName = 'Phone Unlock Local Pairing'
$script:PhoneUnlockVpnFirewallName = 'Phone Unlock Private VPN'
$script:PhoneUnlockLogonPolicyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
$script:PhoneUnlockDefaultProviderValue = 'DefaultCredentialProvider'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '관리자 PowerShell에서 실행해야 합니다.'
    }
    if (-not [Environment]::Is64BitProcess) {
        throw '64비트 PowerShell에서 실행해야 합니다.'
    }
}

function Get-PhoneUnlockInstallRoot {
    $root = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'PhoneUnlock'))
    $programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
    if (-not $root.StartsWith($programFilesRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "안전하지 않은 설치 경로입니다: $root"
    }
    return $root
}

function Send-PhoneUnlockSetupRequest {
    param(
        [Parameter(Mandatory)] [hashtable] $Request,
        [int] $TimeoutMilliseconds = 5000
    )

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'PhoneUnlock.Setup', [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect($TimeoutMilliseconds)
        $utf8 = [Text.UTF8Encoding]::new($false)
        $writer = [IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
        $reader = [IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
        try {
            $writer.AutoFlush = $true
            $writeTask = $writer.WriteLineAsync(($Request | ConvertTo-Json -Compress))
            if (-not $writeTask.Wait($TimeoutMilliseconds)) {
                throw 'Phone Unlock 서비스 요청 쓰기 시간이 초과되었습니다.'
            }
            $readTask = $reader.ReadLineAsync()
            if (-not $readTask.Wait($TimeoutMilliseconds)) {
                throw 'Phone Unlock 서비스 응답 시간이 초과되었습니다.'
            }
            $line = $readTask.Result
            if ([string]::IsNullOrWhiteSpace($line)) {
                throw 'Phone Unlock 서비스 응답이 비어 있습니다.'
            }
            return $line | ConvertFrom-Json
        }
        finally {
            try { $reader.Dispose() } catch { }
            try { $writer.Dispose() } catch { }
        }
    }
    finally {
        try { $pipe.Dispose() } catch { }
    }
}
