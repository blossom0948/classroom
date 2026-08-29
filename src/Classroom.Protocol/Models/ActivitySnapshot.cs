namespace Blossom.Classroom.Protocol.Models;

public sealed record ActivitySnapshot(
    string ApplicationDisplayName,
    string ProcessName,
    string? BrowserDomain,
    string? WindowTitle,
    DateTimeOffset ObservedAtUtc);

