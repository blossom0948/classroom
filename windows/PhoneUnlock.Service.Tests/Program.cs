using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

var tests = new (string Name, Func<Task> Run)[]
{
    ("device token is random and fixed-time verifiable", TestTokenAsync),
    ("pairing token is one-use and stores a P-256 phone", TestPairingAsync),
    ("invalid phone public keys are rejected", TestInvalidPublicKeyAsync),
    ("pairing audit records keep the phone and remote IP", TestAuditAsync),
    ("authentication requests are rate limited", TestAuthenticationRateLimitAsync)
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
    await WithCoordinatorAsync(async (coordinator, store, _) =>
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
    await WithCoordinatorAsync(async (coordinator, _, _) =>
    {
        var pairing = await coordinator.CreateAsync();
        await AssertThrowsAsync<ArgumentException>(() => coordinator.PairAsync(
            pairing.PairingToken,
            new PairRequest(Guid.NewGuid().ToString(), "Bad key phone", Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)))));
    });
}

static async Task TestAuditAsync()
{
    await WithCoordinatorAsync(async (coordinator, _, auditLog) =>
    {
        var pairing = await coordinator.CreateAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var phoneId = Guid.NewGuid().ToString();
        await coordinator.PairAsync(
            pairing.PairingToken,
            new PairRequest(phoneId, "Audit phone", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
            "192.168.10.24");

        var entries = await auditLog.GetRecentAsync();
        var entry = entries.FirstOrDefault(item => item.EventType == "PAIRING");
        Assert(entry is not null, "pairing audit event was not persisted");
        Assert(entry!.PhoneId == phoneId && entry.PhoneName == "Audit phone", "audit event lost phone identity");
        Assert(entry.RemoteIp == "192.168.10.24", "audit event lost remote IP");
    });
}

static Task TestAuthenticationRateLimitAsync()
{
    var limiter = new AuthenticationRequestLimiter();
    var now = DateTimeOffset.UtcNow;
    Assert(limiter.TryAcquire("pc|sid|phone", now).Allowed, "first request was rejected");
    Assert(limiter.TryAcquire("pc|sid|phone", now.AddSeconds(1)).Allowed, "second request was rejected");
    Assert(limiter.TryAcquire("pc|sid|phone", now.AddSeconds(2)).Allowed, "third request was rejected");
    var blocked = limiter.TryAcquire("pc|sid|phone", now.AddSeconds(3));
    Assert(!blocked.Allowed && blocked.RetryAfter >= TimeSpan.FromSeconds(29), "fourth request was not cooled down");
    Assert(limiter.TryAcquire("other-pc|sid|phone", now.AddSeconds(3)).Allowed,
        "one PC incorrectly rate limited another PC");
    Assert(limiter.TryAcquire("pc|sid|phone", now.AddSeconds(64)).Allowed,
        "request stayed limited after the rolling window");
    return Task.CompletedTask;
}

static async Task WithCoordinatorAsync(Func<PairingCoordinator, ConfigurationStore, AuditLogStore, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "PhoneUnlockTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var paths = new ServicePaths(directory, RestrictPermissions: false);
        var store = new ConfigurationStore(paths);
        var auditLog = new AuditLogStore(paths, NullLogger<AuditLogStore>.Instance);
        var coordinator = new PairingCoordinator(store, new CertificateManager(paths), auditLog);
        await test(coordinator, store, auditLog);
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
