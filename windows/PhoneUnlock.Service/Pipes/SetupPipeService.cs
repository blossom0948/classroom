using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class SetupPipeService(
    ConfigurationStore configurationStore,
    WindowsCredentialStore credentialStore,
    WindowsAccountValidator accountValidator,
    PairingCoordinator pairingCoordinator,
    PhoneAuthenticationCoordinator authenticationCoordinator,
    PhoneConnectionRegistry connectionRegistry,
    AuditLogStore auditLog,
    CertificateManager certificateManager,
    PresenceSensorClient presenceSensorClient,
    WindowsSecretStore secretStore,
    AgentConnectionState agentConnectionState,
    ILogger<SetupPipeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = SecureNamedPipe.Create(ServiceConstants.SetupPipeName);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = HandleClientAsync(pipe, stoppingToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            SetupResponse response;
            try
            {
                var line = await PipeTextProtocol.ReadLineAsync(pipe, cancellationToken)
                    ?? throw new InvalidDataException("Setup request was empty.");
                var request = ProtocolJson.Deserialize<SetupRequest>(line);
                response = await ExecuteRequestAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or Win32Exception)
            {
                logger.LogWarning("Rejected setup request: {Reason}", exception.Message);
                response = new SetupResponse(false, "INVALID_REQUEST", exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Setup pipe request failed");
                response = new SetupResponse(false, "INTERNAL_ERROR", "The Phone Unlock service could not complete the request.");
            }

            await PipeTextProtocol.WriteLineAsync(pipe, ProtocolJson.SerializeCompact(response), cancellationToken);
        }
    }

    private async Task<SetupResponse> ExecuteRequestAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            SetupCommands.Status => await GetStatusResponseAsync(cancellationToken),
            SetupCommands.CreatePairing => await CreatePairingResponseAsync(cancellationToken),
            SetupCommands.StoreCredential => await StoreCredentialAsync(request, cancellationToken),
            SetupCommands.DeleteCredential => await DeleteCredentialAsync(cancellationToken),
            SetupCommands.RemovePhone => await RemovePhoneAsync(request, cancellationToken),
            SetupCommands.TestAuthentication => await TestAuthenticationAsync(cancellationToken),
            SetupCommands.SetPreferredPhone => await SetPreferredPhoneAsync(request, cancellationToken),
            SetupCommands.GetAuditLog => await GetAuditLogAsync(request, cancellationToken),
            SetupCommands.Diagnostics => await GetDiagnosticsAsync(cancellationToken),
            SetupCommands.SetProximityLock => await SetProximityLockAsync(request, cancellationToken),
            SetupCommands.SetProximityUnlock => await SetProximityUnlockAsync(request, cancellationToken),
            SetupCommands.SetAutoLockProfile => await SetAutoLockProfileAsync(request, cancellationToken),
            SetupCommands.SetBluetoothRssi => await SetBluetoothRssiAsync(request, cancellationToken),
            SetupCommands.SetRemoteUnlock => await SetRemoteUnlockAsync(request, cancellationToken),
            SetupCommands.SetPresenceSensor => await SetPresenceSensorAsync(request, cancellationToken),
            SetupCommands.ListSmartThingsSensors => await ListSmartThingsSensorsAsync(request, cancellationToken),
            _ => new SetupResponse(false, "UNKNOWN_COMMAND", "Unknown setup command.")
        };
    }

    private async Task<SetupResponse> GetStatusResponseAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        var status = new SetupStatus(
            configuration.ComputerId,
            configuration.ComputerName,
            credentialStore.Exists(),
            configuration.ConfiguredAccountSid,
            configuration.ConfiguredQualifiedUsername,
            configuration.Phones.Select(phone => new PhoneStatus(
                phone.PhoneId,
                phone.PhoneName,
                phone.Enabled,
                connectionRegistry.IsConnected(phone.PhoneId),
                phone.LastSeen)).ToArray(),
            configuration.PreferredPhoneId,
            configuration.ProximityLockEnabled,
            configuration.ProximityUnlockEnabled,
            configuration.ProximityGraceSeconds,
            configuration.AutoLockProfile,
            configuration.BluetoothRssiEnabled,
            configuration.BluetoothRssiThreshold,
            configuration.RemoteUnlockEnabled,
            configuration.PresenceSensorEnabled,
            configuration.PresenceSensorProtocol,
            configuration.PresenceSensorBaseUrl,
            configuration.PresenceSensorEntityId,
            configuration.PresenceSensorComponentId,
            configuration.PresenceSensorCapabilityId,
            configuration.PresenceSensorAttributeName,
            configuration.PresenceSensorGraceSeconds,
            configuration.LastSuccessfulPhoneAuth,
            credentialStore.Exists()
                && configuration.Phones.Any(phone => phone.Enabled)
                && configuration.LastSuccessfulPhoneAuth > DateTimeOffset.UtcNow.AddMinutes(-10),
            agentConnectionState.IsConnected);
        return new SetupResponse(true, "OK", "Service status loaded.", ProtocolJson.Serialize(status));
    }

    private async Task<SetupResponse> CreatePairingResponseAsync(CancellationToken cancellationToken)
    {
        var pairing = await pairingCoordinator.CreateAsync(cancellationToken);
        return new SetupResponse(true, "OK", "Pairing session created for 2 minutes.", ProtocolJson.SerializeCompact(pairing));
    }

    private async Task<SetupResponse> StoreCredentialAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        var credential = accountValidator.Validate(
            request.QualifiedUsername ?? throw new ArgumentException("Qualified username is required."),
            request.Password ?? throw new ArgumentException("Password is required."));
        credentialStore.Save(credential);
        await configurationStore.UpdateAsync(configuration => configuration with
        {
            ConfiguredAccountSid = credential.Sid,
            ConfiguredQualifiedUsername = credential.QualifiedUsername,
            LastSuccessfulPhoneAuth = null
        }, cancellationToken);
        return new SetupResponse(true, "OK", "Windows account credential was validated and stored in Windows Credential Manager.");
    }

    private async Task<SetupResponse> DeleteCredentialAsync(CancellationToken cancellationToken)
    {
        credentialStore.Delete();
        await configurationStore.UpdateAsync(configuration => configuration with
        {
            ConfiguredAccountSid = null,
            ConfiguredQualifiedUsername = null,
            LastSuccessfulPhoneAuth = null
        }, cancellationToken);
        return new SetupResponse(true, "OK", "Stored Windows credential was removed.");
    }

    private async Task<SetupResponse> RemovePhoneAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        var phoneId = request.PhoneId ?? throw new ArgumentException("phoneId is required.");
        await configurationStore.UpdateAsync(configuration => configuration with
        {
            Phones = configuration.Phones
                .Where(phone => !string.Equals(phone.PhoneId, phoneId, StringComparison.Ordinal))
                .ToList(),
            PreferredPhoneId = string.Equals(configuration.PreferredPhoneId, phoneId, StringComparison.Ordinal)
                ? null
                : configuration.PreferredPhoneId,
            LastSuccessfulPhoneAuth = null
        }, cancellationToken);
        await auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            "PHONE",
            "REMOVED",
            phoneId,
            null,
            null,
            null,
            "등록된 휴대폰 삭제",
            Suspicious: false), cancellationToken);
        return new SetupResponse(true, "OK", "Phone was removed.");
    }

    private async Task<SetupResponse> TestAuthenticationAsync(CancellationToken cancellationToken)
    {
        var outcome = await authenticationCoordinator.RequestAsync(null, cancellationToken);
        return new SetupResponse(outcome.IsSuccess, outcome.Code.ToString().ToUpperInvariant(), outcome.Message);
    }

    private async Task<SetupResponse> SetPreferredPhoneAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        if (request.PhoneId is not null
            && !configuration.Phones.Any(phone => string.Equals(phone.PhoneId, request.PhoneId, StringComparison.Ordinal)))
        {
            return new SetupResponse(false, "PHONE_NOT_FOUND", "선택한 휴대폰이 등록되어 있지 않습니다.");
        }

        await configurationStore.UpdateAsync(
            current => current with { PreferredPhoneId = request.PhoneId },
            cancellationToken);
        return new SetupResponse(true, "OK", request.PhoneId is null
            ? "연결 가능한 휴대폰을 자동으로 선택합니다."
            : "로그인에 사용할 휴대폰을 선택했습니다.");
    }

    private async Task<SetupResponse> GetAuditLogAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var entries = await auditLog.GetRecentAsync(request.Limit ?? 100, cancellationToken);
        return new SetupResponse(true, "OK", "감사 기록을 불러왔습니다.", ProtocolJson.Serialize(entries));
    }

    private async Task<SetupResponse> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        var certificate = certificateManager.LoadOrCreate();
        var diagnostics = new SetupDiagnostics(
            typeof(SetupPipeService).Assembly.GetName().Version?.ToString() ?? "unknown",
            ServiceConstants.Port,
            CertificateManager.GetLocalAddresses().Select(address => address.ToString()).ToArray(),
            CertificateManager.GetSha256Fingerprint(certificate),
            connectionRegistry.GetStatuses(configuration.Phones),
            await auditLog.GetRecentAsync(20, cancellationToken),
            configuration.ProximityLockEnabled,
            configuration.ProximityUnlockEnabled,
            configuration.ProximityGraceSeconds,
            configuration.AutoLockProfile,
            configuration.BluetoothRssiEnabled,
            configuration.BluetoothRssiThreshold,
            configuration.RemoteUnlockEnabled,
            configuration.PresenceSensorEnabled,
            configuration.PresenceSensorProtocol,
            configuration.PresenceSensorBaseUrl,
            configuration.PresenceSensorEntityId,
            configuration.PresenceSensorComponentId,
            configuration.PresenceSensorCapabilityId,
            configuration.PresenceSensorAttributeName,
            configuration.PresenceSensorGraceSeconds,
            agentConnectionState.IsConnected);
        return new SetupResponse(true, "OK", "진단 정보를 불러왔습니다.", ProtocolJson.Serialize(diagnostics));
    }

    private async Task<SetupResponse> SetProximityLockAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null)
        {
            throw new ArgumentException("enabled 값이 필요합니다.");
        }

        var graceSeconds = request.GraceSeconds ?? 30;
        if (graceSeconds is < 10 or > 600)
        {
            throw new ArgumentException("자동 잠금 대기 시간은 10초에서 600초 사이여야 합니다.");
        }

        await configurationStore.UpdateAsync(configuration => configuration with
        {
            ProximityLockEnabled = request.Enabled.Value,
            ProximityGraceSeconds = graceSeconds
        }, cancellationToken);
        return new SetupResponse(true, "OK", request.Enabled.Value
            ? $"휴대폰 연결이 {graceSeconds}초 이상 끊기면 자동 잠금합니다."
            : "휴대폰 연결 끊김 자동 잠금을 껐습니다.");
    }

    private async Task<SetupResponse> SetProximityUnlockAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null)
        {
            throw new ArgumentException("enabled 값이 필요합니다.");
        }

        await configurationStore.UpdateAsync(
            configuration => configuration with { ProximityUnlockEnabled = request.Enabled.Value },
            cancellationToken);
        return new SetupResponse(true, "OK", request.Enabled.Value
            ? "휴대폰 heartbeat만 확인해 잠금화면에서 Phone Unlock을 인증 없이 자동 로그인합니다. 보안 수준이 낮아지는 실험 기능입니다."
            : "휴대폰 근접 자동 잠금 해제를 껐습니다.");
    }

    private async Task<SetupResponse> SetAutoLockProfileAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var profile = request.Profile?.Trim().ToLowerInvariant();
        if (profile is not ("standard" or "home" or "away"))
        {
            throw new ArgumentException("자동 잠금 프로필은 standard, home, away 중 하나여야 합니다.");
        }

        var graceSeconds = profile switch
        {
            "home" => 120,
            "away" => 10,
            _ => 30
        };

        await configurationStore.UpdateAsync(
            configuration => configuration with
            {
                AutoLockProfile = profile,
                ProximityGraceSeconds = graceSeconds
            },
            cancellationToken);
        return new SetupResponse(true, "OK", profile switch
        {
            "home" => "집 프로필 · 휴대폰 이탈 120초 후 잠금",
            "away" => "외출 프로필 · 휴대폰 이탈 10초 후 잠금",
            _ => "표준 프로필 · 휴대폰 이탈 30초 후 잠금"
        });
    }

    private async Task<SetupResponse> SetBluetoothRssiAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null)
        {
            throw new ArgumentException("enabled 값이 필요합니다.");
        }

        var threshold = request.RssiThreshold ?? -75;
        if (threshold is < -100 or > -30)
        {
            throw new ArgumentException("Bluetooth RSSI 기준은 -100에서 -30 사이여야 합니다.");
        }

        await configurationStore.UpdateAsync(configuration => configuration with
        {
            BluetoothRssiEnabled = request.Enabled.Value,
            BluetoothRssiThreshold = threshold
        }, cancellationToken);
        return new SetupResponse(true, "OK", request.Enabled.Value
            ? $"Bluetooth RSSI 거리 기준을 {threshold} dBm으로 설정했습니다."
            : "Bluetooth RSSI 거리 측정을 껐습니다.");
    }

    private async Task<SetupResponse> SetRemoteUnlockAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null)
        {
            throw new ArgumentException("enabled 값이 필요합니다.");
        }

        await configurationStore.UpdateAsync(
            configuration => configuration with { RemoteUnlockEnabled = request.Enabled.Value },
            cancellationToken);
        return new SetupResponse(true, "OK", request.Enabled.Value
            ? "휴대폰 앱의 원격 잠금 해제를 허용했습니다."
            : "휴대폰 원격 잠금 해제를 껐습니다.");
    }

    private async Task<SetupResponse> SetPresenceSensorAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is null)
        {
            throw new ArgumentException("enabled 값이 필요합니다.");
        }

        var graceSeconds = request.GraceSeconds ?? 10;
        if (graceSeconds is < 10 or > 600)
        {
            throw new ArgumentException("재실 센서 자동 잠금 대기 시간은 10초에서 600초 사이여야 합니다.");
        }

        var protocol = request.SensorProtocol?.Trim().ToLowerInvariant() ?? "zigbee";
        if (protocol is not ("zigbee" or "matter" or "smartthings"))
        {
            throw new ArgumentException("재실 센서 방식은 Zigbee, Matter, SmartThings 중 하나여야 합니다.");
        }

        var componentId = string.IsNullOrWhiteSpace(request.ComponentId)
            ? "main"
            : request.ComponentId.Trim();
        var capabilityId = string.IsNullOrWhiteSpace(request.CapabilityId)
            ? "occupancySensor"
            : request.CapabilityId.Trim();
        var attributeName = string.IsNullOrWhiteSpace(request.AttributeName)
            ? "occupancy"
            : request.AttributeName.Trim();
        if (componentId.Length > 64 || capabilityId.Length > 64 || attributeName.Length > 64)
        {
            throw new ArgumentException("센서 component, capability, attribute 이름이 너무 깁니다.");
        }

        if (!request.Enabled.Value)
        {
            await configurationStore.UpdateAsync(configuration => configuration with
            {
                PresenceSensorEnabled = false,
                PresenceSensorProtocol = protocol,
                PresenceSensorGraceSeconds = graceSeconds
            }, cancellationToken);
            return new SetupResponse(true, "OK", "재실 센서 자동 잠금을 껐습니다.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(request.Url)
            && protocol == "smartthings"
            ? "https://api.smartthings.com/v1"
            : request.Url?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(request.EntityId)
            || request.EntityId.Length > 256)
        {
            throw new ArgumentException(protocol == "smartthings"
                ? "SmartThings API 주소와 device ID를 확인하세요."
                : "Home Assistant 주소와 entity_id를 확인하세요.");
        }

        var currentToken = secretStore.Read("PhoneUnlock/PresenceSensor");
        if (!string.IsNullOrWhiteSpace(request.Token))
        {
            presenceSensorClient.SaveToken(request.Token.Trim());
        }
        else if (string.IsNullOrWhiteSpace(currentToken))
        {
            throw new ArgumentException(protocol == "smartthings"
                ? "SmartThings Personal Access Token을 입력하세요."
                : "Home Assistant 장기 액세스 토큰을 입력하세요.");
        }

        await configurationStore.UpdateAsync(configuration => configuration with
        {
            PresenceSensorEnabled = true,
            PresenceSensorProtocol = protocol,
            PresenceSensorBaseUrl = endpoint.ToString().TrimEnd('/'),
            PresenceSensorEntityId = request.EntityId.Trim(),
            PresenceSensorComponentId = componentId,
            PresenceSensorCapabilityId = capabilityId,
            PresenceSensorAttributeName = attributeName,
            PresenceSensorGraceSeconds = graceSeconds
        }, cancellationToken);
        var source = protocol == "smartthings" ? "SmartThings" : protocol == "matter" ? "Matter" : "Zigbee";
        return new SetupResponse(true, "OK", $"{source} 재실 센서 연동 완료 · 감지 해제 후 {graceSeconds}초 뒤 잠금합니다.");
    }

    private async Task<SetupResponse> ListSmartThingsSensorsAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Url)
            ? "https://api.smartthings.com/v1"
            : request.Url.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("SmartThings API 주소를 확인하세요.");
        }

        var token = string.IsNullOrWhiteSpace(request.Token)
            ? secretStore.Read("PhoneUnlock/PresenceSensor")
            : request.Token.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("SmartThings Personal Access Token을 입력하세요.");
        }

        var sensors = await presenceSensorClient.ListSmartThingsSensorsAsync(
            endpoint.ToString().TrimEnd('/'),
            token,
            cancellationToken);
        return new SetupResponse(true, "OK", sensors.Count == 0
            ? "SmartThings에서 재실·동작 센서를 찾지 못했습니다."
            : $"SmartThings 센서 {sensors.Count}개를 찾았습니다.",
            ProtocolJson.Serialize(sensors));
    }
}
