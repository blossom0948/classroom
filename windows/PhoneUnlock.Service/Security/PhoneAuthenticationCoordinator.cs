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
        try
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(configuration.ConfiguredAccountSid))
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.NotConfigured, "Windows account credential is not configured.");
            }

            if (!string.IsNullOrWhiteSpace(expectedSid)
                && !string.Equals(configuration.ConfiguredAccountSid, expectedSid, StringComparison.OrdinalIgnoreCase))
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.NotConfigured, "Phone Unlock is not configured for this Windows account.");
            }

            var connected = await connectionRegistry.GetConnectedPhoneAsync(cancellationToken);
            if (connected is null)
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.PhoneOffline, "Paired phone is offline.");
            }

            var request = challengeGenerator.Create(
                configuration.ComputerId,
                configuration.ComputerName,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(ServiceConstants.AuthenticationTimeoutSeconds));
            challengeStore.Register(request.Payload);
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
                return new PhoneAuthOutcome(PhoneAuthResultCode.Timeout, "Phone authentication timed out.");
            }
            catch (IOException)
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.PhoneOffline, "Phone disconnected during authentication.");
            }

            using var document = JsonDocument.Parse(responseJson);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type == ProtocolConstants.AuthDenied)
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.Denied, "Login was denied on the phone.");
            }

            if (type == ProtocolConstants.AuthExpired)
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.Expired, "Phone reported that the request expired.");
            }

            if (type != ProtocolConstants.AuthApproved)
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone returned an unsupported response.");
            }

            var response = ProtocolJson.Deserialize<ProtocolEnvelope<AuthApprovedPayload>>(responseJson);
            if (!string.Equals(response.Payload.PhoneId, connected.Value.Phone.PhoneId, StringComparison.Ordinal))
            {
                return new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone identity did not match the authenticated connection.");
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
                return new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, $"Signature response rejected: {status}.");
            }

            await configurationStore.UpdateAsync(
                current => current with { LastSuccessfulPhoneAuth = DateTimeOffset.UtcNow },
                cancellationToken);
            logger.LogInformation(
                "AUTH_SUCCESS phone={PhoneId} request={RequestId}",
                connected.Value.Phone.PhoneId,
                request.Payload.RequestId.ToString("N")[..8]);
            return new PhoneAuthOutcome(PhoneAuthResultCode.Success, "Phone biometric signature verified.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed phone authentication response");
            return new PhoneAuthOutcome(PhoneAuthResultCode.InvalidResponse, "Phone response JSON was invalid.");
        }
        finally
        {
            authenticationGate.Release();
        }
    }
}
