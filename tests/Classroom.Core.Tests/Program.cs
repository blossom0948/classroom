using System.Collections.Concurrent;
using Blossom.Classroom.Core.Audit;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Core.Serialization;

var tests = new (string Name, Action Run)[]
{
    ("Base64URL round trips arbitrary bytes", Base64UrlRoundTrip),
    ("device tokens are random and fixed-time verifiable", TokenSecurityWorks),
    ("replay guard accepts once and rejects replay", ReplayGuardRejectsReplay),
    ("replay guard rejects expired entries", ReplayGuardRejectsExpired),
    ("replay guard is safe under concurrent access", ReplayGuardIsAtomic),
    ("audit events contain the required identity fields", AuditEventContainsRequiredFields),
    ("JSON uses stable camelCase and omits nulls", JsonShapeIsStable)
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

static void Base64UrlRoundTrip()
{
    var bytes = Enumerable.Range(0, 255).Select(value => (byte)value).ToArray();
    var encoded = Base64Url.Encode(bytes);
    Assert(!encoded.Contains('=') && !encoded.Contains('+') && !encoded.Contains('/'), "Encoding was not URL-safe.");
    Assert(bytes.SequenceEqual(Base64Url.Decode(encoded)), "Decoded bytes differ.");
}

static void TokenSecurityWorks()
{
    var first = TokenSecurity.CreateToken();
    var second = TokenSecurity.CreateToken();
    var hash = TokenSecurity.HashToken(first);
    Assert(first.Length >= 43 && first != second, "Tokens were not independent 256-bit values.");
    Assert(TokenSecurity.VerifyToken(first, hash), "Valid token was rejected.");
    Assert(!TokenSecurity.VerifyToken(second, hash), "Different token was accepted.");
    Assert(!TokenSecurity.VerifyToken(first, "not-a-hash"), "Malformed hash was accepted.");
}

static void ReplayGuardRejectsReplay()
{
    var guard = new ReplayGuard();
    var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
    Assert(guard.TryAccept("request-1", now.AddMinutes(1), now), "First request was rejected.");
    Assert(!guard.TryAccept("request-1", now.AddMinutes(1), now), "Replay was accepted.");
    Assert(guard.Contains("request-1", now.AddSeconds(10)), "Accepted request disappeared.");
}

static void ReplayGuardRejectsExpired()
{
    var guard = new ReplayGuard();
    var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
    Assert(!guard.TryAccept("expired", now, now), "Expired request was accepted.");
}

static void ReplayGuardIsAtomic()
{
    var guard = new ReplayGuard();
    var now = DateTimeOffset.UtcNow;
    var results = new ConcurrentBag<bool>();
    Parallel.For(0, 32, _ => results.Add(guard.TryAccept("same-request", now.AddMinutes(1), now)));
    Assert(results.Count(result => result) == 1, "More than one concurrent request was accepted.");
}

static void AuditEventContainsRequiredFields()
{
    var schoolId = Guid.NewGuid();
    var classId = Guid.NewGuid();
    var sessionId = Guid.NewGuid();
    var teacherId = Guid.NewGuid();
    var studentId = Guid.NewGuid();
    var requestId = Guid.NewGuid();
    var entry = AuditEvent.Create(
        "COMMAND",
        "SUCCESS",
        schoolId: schoolId,
        classId: classId,
        sessionId: sessionId,
        teacherId: teacherId,
        studentId: studentId,
        requestId: requestId,
        timestampUtc: DateTimeOffset.Parse("2026-08-28T00:00:00Z"));

    Assert(entry.EventId != Guid.Empty, "Event ID was not generated.");
    Assert(entry.TimestampUtc == DateTimeOffset.Parse("2026-08-28T00:00:00Z"), "Timestamp was not normalized.");
    Assert(entry.SchoolId == schoolId && entry.ClassId == classId && entry.SessionId == sessionId, "Class identity was lost.");
    Assert(entry.TeacherId == teacherId && entry.StudentId == studentId && entry.RequestId == requestId, "Actor identity was lost.");
}

static void JsonShapeIsStable()
{
    var json = ClassroomJson.Serialize(new { DeviceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Optional = (string?)null });
    Assert(json.Contains("\"deviceId\""), "camelCase property was not emitted.");
    Assert(!json.Contains("Optional", StringComparison.Ordinal), "Null optional property was not omitted.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

