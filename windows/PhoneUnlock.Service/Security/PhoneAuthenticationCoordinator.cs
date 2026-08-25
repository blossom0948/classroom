using System.Net.WebSockets;
using System.Text.Json;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Core.Security;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class PhoneAuthenticationCoordinator(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    AuditLogStore auditLog,
    ILogger<PhoneAuthenticationCoordinator> logger)
{
    private readonly ChallengeGenerator challengeGenerator = new();
    private readonly ChallengeStore challengeStore = new();
    private readonly SignatureVerifier signatureVerifier = new();
    private readonly SemaphoreSlim authenticationGate = new(1, 1);

    public async Task<PhoneAuthOutcome> RequestAsync(
        string? expectedSid,
        CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken);
        PairedPhoneRecord? selectedPhone = null;
        PhoneConnection? selectedConnection = null;
        Guid? requestId = null;
        try
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(configuration.ConfiguredAccountSid))
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.NotConfigured, "Windows account credential is not configured."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }

            if (!string.IsNullOrWhiteSpace(expectedSid)
                && !string.Equals(configuration.ConfiguredAccountSid, expectedSid, StringComparison.OrdinalIgnoreCase))
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.NotConfigured, "Phone Unlock is not configured for this Windows account."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: true);
            }

            var connected = await connectionRegistry.GetConnectedPhoneAsync(
                configuration.PreferredPhoneId,
                cancellationToken);
            if (connected is null)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.PhoneOffline, "Paired phone is offline."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }

            selectedPhone = connected.Value.Phone;
            selectedConnection = connected.Value.Connection;

            var request = challengeGenerator.Create(
                configuration.ComputerId,
                configuration.ComputerName,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(ServiceConstants.AuthenticationTimeoutSeconds));
            challengeStore.Register(request.Payload);
            requestId = request.Payload.RequestId;
            logger.LogInformation(
                "AUTH_REQUEST phone={PhoneId} request={RequestId}",
                connected.Value.Phone.PhoneId,
                request.Payload.RequestId.ToString("N")[..8]);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(ServiceConstants.AuthenticationTimeoutSeconds));

            string responseJson;
            try
            {
                responseJson = await connected.Value.Connection.SendAuthRequestAsync(
                    request.Payload.RequestId,
                    ProtocolJson.Serialize(request),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.Timeout, "Phone authentication timed out."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }
            catch (IOException)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.PhoneOffline, "Phone disconnected during authentication."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }

            using var document = JsonDocument.Parse(responseJson);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type == ProtocolConstants.AuthDenied)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.Denied, "Login was denied on the phone."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }

            if (type == ProtocolConstants.AuthExpired)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.Expired, "Phone reported that the request expired."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: false);
            }

            if (type != ProtocolConstants.AuthApproved)
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone returned an unsupported response."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: true);
            }

            var response = ProtocolJson.Deserialize<ProtocolEnvelope<AuthApprovedPayload>>(responseJson);
            if (!string.Equals(response.Payload.PhoneId, connected.Value.Phone.PhoneId, StringComparison.Ordinal))
            {
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone identity did not match the authenticated connection."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: true);
            }

            var validator = new AuthValidationService(challengeStore, signatureVerifier);
            var status = validator.Verify(response, connected.Value.Phone.PublicKey);
            if (status != AuthValidationStatus.Success)
            {
                logger.LogWarning(
                    "AUTH_RESPONSE rejected phone={PhoneId} request={RequestId} status={Status}",
                    connected.Value.Phone.PhoneId,
                    request.Payload.RequestId.ToString("N")[..8],
                    status);
                return await CompleteAsync(
                    new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, $"Signature response rejected: {status}."),
                    selectedPhone,
                    selectedConnection,
                    requestId,
                    suspicious: true);
            }

            await configurationStore.UpdateAsync(
                current => current with { LastSuccessfulPhoneAuth = DateTimeOffset.UtcNow },
                cancellationToken);
            logger.LogInformation(
                "AUTH_SUCCESS phone={PhoneId} request={RequestId}",
                connected.Value.Phone.PhoneId,
                request.Payload.RequestId.ToString("N")[..8]);
            return await CompleteAsync(
                new PhoneAuthOutcome(PhoneAuthResultCode.Success, "Phone authentication signature verified."),
                selectedPhone,
                selectedConnection,
                requestId,
                suspicious: false);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed phone authentication response");
            return await CompleteAsync(
                new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone response JSON was invalid."),
                selectedPhone,
                selectedConnection,
                requestId,
                suspicious: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                new PhoneAuthOutcome(PhoneAuthResultCode.Timeout, "Phone authentication timed out."),
                selectedPhone,
                selectedConnection,
                requestId,
                suspicious: false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Phone authentication failed unexpectedly");
            return await CompleteAsync(
                new PhoneAuthOutcome(PhoneAuthResultCode.InternalError, "Phone authentication failed unexpectedly."),
                selectedPhone,
                selectedConnection,
                requestId,
                suspicious: true);
        }
        finally
        {
            authenticationGate.Release();
        }
    }

    private async Task<PhoneAuthOutcome> CompleteAsync(
        PhoneAuthOutcome outcome,
        PairedPhoneRecord? phone,
        PhoneConnection? connection,
        Guid? requestId,
        bool suspicious)
    {
        await auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            "AUTHENTICATION",
            outcome.Code.ToString().ToUpperInvariant(),
            phone?.PhoneId,
            phone?.PhoneName,
            connection?.RemoteIp,
            requestId,
            outcome.Message,
            suspicious), CancellationToken.None);
        if (suspicious && connection is { IsOpen: true })
        {
            try
            {
                await connection.SendSecurityAlertAsync(
                    "의심스러운 Windows 로그인 요청이 거부되었습니다. PC 보안 기록을 확인하세요.",
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException)
            {
                logger.LogInformation("Could not deliver security alert to phone: {Message}", exception.Message);
            }
        }
        return outcome;
    }
}
