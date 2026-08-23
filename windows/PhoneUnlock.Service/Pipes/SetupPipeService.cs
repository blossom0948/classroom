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
            configuration.LastSuccessfulPhoneAuth,
            credentialStore.Exists()
                && configuration.Phones.Any(phone => phone.Enabled)
                && configuration.LastSuccessfulPhoneAuth > DateTimeOffset.UtcNow.AddMinutes(-10));
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
            LastSuccessfulPhoneAuth = null
        }, cancellationToken);
        return new SetupResponse(true, "OK", "Phone was removed.");
    }

    private async Task<SetupResponse> TestAuthenticationAsync(CancellationToken cancellationToken)
    {
        var outcome = await authenticationCoordinator.RequestAsync(null, cancellationToken);
        return new SetupResponse(outcome.IsSuccess, outcome.Code.ToString().ToUpperInvariant(), outcome.Message);
    }
}
