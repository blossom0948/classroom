using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class AuthPipeService(
    PhoneAuthenticationCoordinator authenticationCoordinator,
    WindowsCredentialStore credentialStore,
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    AuditLogStore auditLog,
    ILogger<AuthPipeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = SecureNamedPipe.Create(ServiceConstants.AuthPipeName);
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
            try
            {
                var line = await PipeTextProtocol.ReadLineAsync(pipe, cancellationToken);
                var proximityOnly = line?.StartsWith("PROXIMITY|", StringComparison.Ordinal) == true;
                var phoneApproval = line?.StartsWith("AUTH|", StringComparison.Ordinal) == true;
                if (line is null || (!proximityOnly && !phoneApproval))
                {
                    await auditLog.AppendAsync(new Models.AuditEntry(
                        DateTimeOffset.UtcNow,
                        "CREDENTIAL_PROVIDER",
                        "REJECTED",
                        null,
                        null,
                        null,
                        null,
                        "Credential Provider 요청 형식이 올바르지 않음",
                        Suspicious: true), CancellationToken.None);
                    await WriteErrorAsync(pipe, "INVALID_REQUEST", "Credential Provider request is invalid.", cancellationToken);
                    return;
                }

                var prefixLength = proximityOnly ? "PROXIMITY|".Length : "AUTH|".Length;
                var sid = line[prefixLength..];
                if (!IsSidText(sid))
                {
                    await auditLog.AppendAsync(new Models.AuditEntry(
                        DateTimeOffset.UtcNow,
                        "CREDENTIAL_PROVIDER",
                        "REJECTED",
                        null,
                        null,
                        null,
                        null,
                        "Credential Provider가 잘못된 사용자 SID를 보냄",
                        Suspicious: true), CancellationToken.None);
                    await WriteErrorAsync(pipe, "INVALID_SID", "Windows account SID is invalid.", cancellationToken);
                    return;
                }

                if (proximityOnly)
                {
                    await HandleProximityUnlockAsync(pipe, sid, cancellationToken);
                    return;
                }

                var outcome = await authenticationCoordinator.RequestAsync(sid, cancellationToken);
                if (!outcome.IsSuccess)
                {
                    await WriteErrorAsync(pipe, outcome.Code.ToString().ToUpperInvariant(), outcome.Message, cancellationToken);
                    return;
                }

                var credential = credentialStore.Read();
                if (credential is null || !string.Equals(credential.Sid, sid, StringComparison.OrdinalIgnoreCase))
                {
                    await auditLog.AppendAsync(new Models.AuditEntry(
                        DateTimeOffset.UtcNow,
                        "CREDENTIAL_PROVIDER",
                        "REJECTED",
                        null,
                        null,
                        null,
                        null,
                        "인증된 휴대폰 요청과 Windows 계정 자격 증명이 일치하지 않음",
                        Suspicious: true), CancellationToken.None);
                    await WriteErrorAsync(pipe, "CREDENTIAL_MISMATCH", "Stored Windows account does not match this tile.", cancellationToken);
                    return;
                }

                await WriteCredentialAsync(pipe, credential, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                logger.LogInformation("Credential Provider pipe ended: {Reason}", exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Credential Provider request failed");
                if (pipe.IsConnected)
                {
                    await WriteErrorAsync(pipe, "INTERNAL_ERROR", "Phone Unlock service failed.", CancellationToken.None);
                }
            }
        }
    }

    private async Task HandleProximityUnlockAsync(
        NamedPipeServerStream pipe,
        string sid,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        if (!configuration.ProximityUnlockEnabled)
        {
            await auditLog.AppendAsync(new Models.AuditEntry(
                DateTimeOffset.UtcNow,
                "PROXIMITY_UNLOCK",
                "DISABLED",
                null,
                null,
                null,
                null,
                "근접 자동 잠금 해제가 설정에서 꺼져 있음",
                Suspicious: false), CancellationToken.None);
            await WriteErrorAsync(pipe, "PROXIMITY_DISABLED", "Proximity unlock is disabled.", cancellationToken);
            return;
        }

        if (!string.Equals(configuration.ConfiguredAccountSid, sid, StringComparison.OrdinalIgnoreCase))
        {
            await auditLog.AppendAsync(new Models.AuditEntry(
                DateTimeOffset.UtcNow,
                "PROXIMITY_UNLOCK",
                "REJECTED",
                null,
                null,
                null,
                null,
                "근접 자동 잠금 해제 요청의 Windows 계정이 일치하지 않음",
                Suspicious: true), CancellationToken.None);
            await WriteErrorAsync(pipe, "CREDENTIAL_MISMATCH", "Stored Windows account does not match this tile.", cancellationToken);
            return;
        }

        var connected = await connectionRegistry.GetConnectedPhoneAsync(
            configuration.PreferredPhoneId,
            cancellationToken);
        if (connected is null || !connectionRegistry.HasRecentHeartbeat(connected.Value.Phone.PhoneId, TimeSpan.FromSeconds(20)))
        {
            await auditLog.AppendAsync(new Models.AuditEntry(
                DateTimeOffset.UtcNow,
                "PROXIMITY_UNLOCK",
                "PHONE_OFFLINE",
                connected?.Phone.PhoneId,
                connected?.Phone.PhoneName,
                connected?.Connection.RemoteIp,
                null,
                "근접 자동 잠금 해제 시점에 휴대폰 heartbeat가 확인되지 않음",
                Suspicious: false), CancellationToken.None);
            await WriteErrorAsync(pipe, "PHONE_OFFLINE", "Trusted phone is not currently present.", cancellationToken);
            return;
        }

        var credential = credentialStore.Read();
        if (credential is null || !string.Equals(credential.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            await auditLog.AppendAsync(new Models.AuditEntry(
                DateTimeOffset.UtcNow,
                "PROXIMITY_UNLOCK",
                "CREDENTIAL_MISMATCH",
                connected.Value.Phone.PhoneId,
                connected.Value.Phone.PhoneName,
                connected.Value.Connection.RemoteIp,
                null,
                "근접 자동 잠금 해제에 사용할 Windows 자격 증명이 없음 또는 SID 불일치",
                Suspicious: true), CancellationToken.None);
            await WriteErrorAsync(pipe, "CREDENTIAL_MISMATCH", "Stored Windows account does not match this tile.", cancellationToken);
            return;
        }

        await auditLog.AppendAsync(new Models.AuditEntry(
            DateTimeOffset.UtcNow,
            "PROXIMITY_UNLOCK",
            "SUCCESS",
            connected.Value.Phone.PhoneId,
            connected.Value.Phone.PhoneName,
            connected.Value.Connection.RemoteIp,
            null,
            "신뢰된 휴대폰 근접 신호로 Windows 잠금 해제",
            Suspicious: false), CancellationToken.None);
        await WriteCredentialAsync(pipe, credential, cancellationToken);
    }

    private static async Task WriteCredentialAsync(
        Stream stream,
        Models.StoredWindowsCredential credential,
        CancellationToken cancellationToken)
    {
        var domain = Encoding.Unicode.GetBytes(credential.Domain);
        var username = Encoding.Unicode.GetBytes(credential.Username);
        var password = Encoding.Unicode.GetBytes(credential.Password);
        try
        {
            var response = string.Join('|',
                "SUCCESS",
                Convert.ToBase64String(domain),
                Convert.ToBase64String(username),
                Convert.ToBase64String(password));
            await PipeTextProtocol.WriteLineAsync(stream, response, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static Task WriteErrorAsync(Stream stream, string code, string message, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        return PipeTextProtocol.WriteLineAsync(stream, $"ERROR|{code}|{encoded}", cancellationToken);
    }

    private static bool IsSidText(string value) =>
        value.Length is >= 8 and <= 184
        && value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        && value.All(character => char.IsAsciiDigit(character) || character is 'S' or 's' or '-');
}
