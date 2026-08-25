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
            ? "휴대폰이 가까워지면 잠금화면의 Phone Unlock 인증을 자동으로 시작합니다. 보안 수준이 낮아지는 실험 기능입니다."
            : "휴대폰 근접 자동 잠금 해제를 껐습니다.");
    }
}
