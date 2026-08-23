using System.Security.Cryptography;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Core.Security;

var tests = new (string Name, Action Run)[]
{
    ("challenge is 32 random bytes", ChallengeIsRandom),
    ("canonical payload is byte stable", CanonicalPayloadIsStable),
    ("valid Android-format DER signature succeeds", ValidSignatureSucceeds),
    ("wrong key and tampering are rejected", WrongKeyAndTamperingAreRejected),
    ("invalid signature does not consume request", InvalidSignatureDoesNotConsume),
    ("expired request is rejected", ExpiredRequestIsRejected),
    ("successful response cannot be replayed", ReplayIsRejected),
    ("pairing token validates value and expiry", PairingTokenValidationWorks)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void ChallengeIsRandom()
{
    var generator = new ChallengeGenerator();
    var computerId = Guid.NewGuid();
    var first = generator.Create(computerId, "TEST-PC").Payload;
    var second = generator.Create(computerId, "TEST-PC").Payload;

    Assert(Convert.FromBase64String(first.Challenge).Length == 32, "First challenge was not 32 bytes.");
    Assert(Convert.FromBase64String(second.Challenge).Length == 32, "Second challenge was not 32 bytes.");
    Assert(first.Challenge != second.Challenge, "Two generated challenges were identical.");
    Assert(first.RequestId != second.RequestId, "Two request IDs were identical.");
}

static void CanonicalPayloadIsStable()
{
    var request = new AuthRequestPayload(
        Guid.Parse("c6a60298-33c4-49dc-b1ed-b1a046fa7347"),
        Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray()),
        1787490000,
        1787490030,
        Guid.Parse("e66aa175-932a-4986-8b7d-1156640470a1"),
        "MY-PC");

    const string expected = "PHONE-UNLOCK-V1\n"
        + "requestId=c6a60298-33c4-49dc-b1ed-b1a046fa7347\n"
        + "computerId=e66aa175-932a-4986-8b7d-1156640470a1\n"
        + "challenge=QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=\n"
        + "expiresAt=1787490030";

    Assert(CanonicalPayload.Create(request) == expected, "Canonical payload changed.");
    Assert(!CanonicalPayload.GetBytes(request).Contains((byte)'\r'), "Canonical payload contains CR.");
}

static void ValidSignatureSucceeds()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var fixture = CreateSignedFixture(key);
    var result = fixture.Service.Verify(fixture.Response, ExportPublicKey(key), fixture.Now);
    Assert(result == AuthValidationStatus.Success, $"Expected success, got {result}.");
}

static void WrongKeyAndTamperingAreRejected()
{
    using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var fixture = CreateSignedFixture(signingKey);

    var wrongKeyResult = fixture.Service.Verify(fixture.Response, ExportPublicKey(wrongKey), fixture.Now);
    Assert(wrongKeyResult == AuthValidationStatus.InvalidPublicKeyOrSignature, "Wrong public key was accepted.");

    var tamperedBytes = Convert.FromBase64String(fixture.Response.Payload.Challenge);
    tamperedBytes[0] ^= 0xFF;
    var tamperedPayload = fixture.Response.Payload with { Challenge = Convert.ToBase64String(tamperedBytes) };
    var tamperedEnvelope = fixture.Response with { Payload = tamperedPayload };
    var tamperedResult = fixture.Service.Verify(tamperedEnvelope, ExportPublicKey(signingKey), fixture.Now);
    Assert(tamperedResult == AuthValidationStatus.RequestMismatch, "Changed challenge was not rejected.");
}

static void InvalidSignatureDoesNotConsume()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var fixture = CreateSignedFixture(key);
    var invalidPayload = fixture.Response.Payload with { Signature = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
    var invalidResponse = fixture.Response with { Payload = invalidPayload };

    var invalidResult = fixture.Service.Verify(invalidResponse, ExportPublicKey(key), fixture.Now);
    var validResult = fixture.Service.Verify(fixture.Response, ExportPublicKey(key), fixture.Now);
    Assert(invalidResult == AuthValidationStatus.InvalidPublicKeyOrSignature, "Malformed signature did not fail.");
    Assert(validResult == AuthValidationStatus.Success, "Invalid attempt consumed the pending request.");
}

static void ExpiredRequestIsRejected()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var fixture = CreateSignedFixture(key);
    var result = fixture.Service.Verify(fixture.Response, ExportPublicKey(key), fixture.Now.AddSeconds(31));
    Assert(result == AuthValidationStatus.Expired, $"Expected expiry, got {result}.");
}

static void ReplayIsRejected()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var fixture = CreateSignedFixture(key);
    var first = fixture.Service.Verify(fixture.Response, ExportPublicKey(key), fixture.Now);
    var second = fixture.Service.Verify(fixture.Response, ExportPublicKey(key), fixture.Now);
    Assert(first == AuthValidationStatus.Success, "First response did not succeed.");
    Assert(second == AuthValidationStatus.Replayed, $"Replay returned {second}.");
}

static void PairingTokenValidationWorks()
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1787490000);
    var token = PairingTokenService.Create(now);
    Assert(token.Value.Length == 43, "256-bit Base64URL token should be 43 characters.");
    Assert(PairingTokenService.Validate(token, token.Value, now.AddSeconds(119)), "Valid pairing token failed.");
    Assert(!PairingTokenService.Validate(token, token.Value + "x", now), "Wrong pairing token succeeded.");
    Assert(!PairingTokenService.Validate(token, token.Value, now.AddSeconds(121)), "Expired pairing token succeeded.");
}

static SignedFixture CreateSignedFixture(ECDsa key)
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1787490000);
    var store = new ChallengeStore();
    var request = new ChallengeGenerator().Create(Guid.NewGuid(), "TEST-PC", now).Payload;
    store.Register(request);

    var unsignedResponse = new AuthApprovedPayload(
        request.RequestId,
        request.ComputerId,
        request.Challenge,
        request.ExpiresAt,
        "test-phone",
        string.Empty);
    var signature = key.SignData(
        CanonicalPayload.GetBytes(unsignedResponse),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    var responsePayload = unsignedResponse with { Signature = Convert.ToBase64String(signature) };
    var response = new ProtocolEnvelope<AuthApprovedPayload>(
        ProtocolConstants.Version,
        ProtocolConstants.AuthApproved,
        Guid.NewGuid(),
        now.AddSeconds(2).ToUnixTimeSeconds(),
        responsePayload);

    return new SignedFixture(
        now,
        new AuthValidationService(store, new SignatureVerifier()),
        response);
}

static string ExportPublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record SignedFixture(
    DateTimeOffset Now,
    AuthValidationService Service,
    ProtocolEnvelope<AuthApprovedPayload> Response);
