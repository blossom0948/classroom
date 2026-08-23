using System.Security.Cryptography;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

var tests = new (string Name, Func<Task> Run)[]
{
    ("device token is random and fixed-time verifiable", TestTokenAsync),
    ("pairing token is one-use and stores a P-256 phone", TestPairingAsync),
    ("invalid phone public keys are rejected", TestInvalidPublicKeyAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} service tests passed.");
return failures.Count == 0 ? 0 : 1;

static Task TestTokenAsync()
{
    var first = TokenSecurity.CreateToken();
    var second = TokenSecurity.CreateToken();
    Assert(first.Length >= 43 && first != second, "tokens should be independent 256-bit values");
    var hash = TokenSecurity.HashToken(first);
    Assert(TokenSecurity.VerifyToken(first, hash), "valid token was rejected");
    Assert(!TokenSecurity.VerifyToken(second, hash), "different token was accepted");
    Assert(!TokenSecurity.VerifyToken(first, "not-base64"), "malformed hash was accepted");
    return Task.CompletedTask;
}

static async Task TestPairingAsync()
{
    await WithCoordinatorAsync(async (coordinator, store) =>
    {
        var pairing = await coordinator.CreateAsync();
        Assert(pairing.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "pairing should not already be expired");
        Assert(pairing.CertificateFingerprint.Length == 64, "certificate fingerprint should be SHA-256");
        Assert(pairing.Hosts.Count > 0 && pairing.Hosts.Contains(pairing.Host), "pairing should expose reachable host candidates");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var phoneId = Guid.NewGuid().ToString();
        var request = new PairRequest(
            phoneId,
            "Test phone",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var response = await coordinator.PairAsync(pairing.PairingToken, request);
        Assert(response is not null && response.DeviceToken.Length >= 43, "pairing should return a device token");
        Assert(await coordinator.PairAsync(pairing.PairingToken, request) is null, "pairing token was accepted twice");

        var configuration = await store.GetAsync();
        Assert(configuration.Phones.Count == 1, "paired phone was not persisted");
        Assert(TokenSecurity.VerifyToken(response!.DeviceToken, configuration.Phones[0].DeviceTokenHash),
            "stored device-token hash does not match response");
    });
}

static async Task TestInvalidPublicKeyAsync()
{
    await WithCoordinatorAsync(async (coordinator, _) =>
    {
        var pairing = await coordinator.CreateAsync();
        await AssertThrowsAsync<ArgumentException>(() => coordinator.PairAsync(
            pairing.PairingToken,
            new PairRequest(Guid.NewGuid().ToString(), "Bad key phone", Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)))));
    });
}

static async Task WithCoordinatorAsync(Func<PairingCoordinator, ConfigurationStore, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "PhoneUnlockTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var paths = new ServicePaths(directory, RestrictPermissions: false);
        var store = new ConfigurationStore(paths);
        var coordinator = new PairingCoordinator(store, new CertificateManager(paths));
        await test(coordinator, store);
    }
    finally
    {
        var resolved = Path.GetFullPath(directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PhoneUnlockTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
