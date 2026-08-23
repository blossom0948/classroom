using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class AuthPipeService(
    PhoneAuthenticationCoordinator authenticationCoordinator,
    WindowsCredentialStore credentialStore,
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
                if (line is null || !line.StartsWith("AUTH|", StringComparison.Ordinal))
                {
                    await WriteErrorAsync(pipe, "INVALID_REQUEST", "Credential Provider request is invalid.", cancellationToken);
                    return;
                }

                var sid = line["AUTH|".Length..];
                if (!IsSidText(sid))
                {
                    await WriteErrorAsync(pipe, "INVALID_SID", "Windows account SID is invalid.", cancellationToken);
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
