namespace PhoneUnlock.Service.Models;

public enum PhoneAuthResultCode
{
    Success,
    PhoneOffline,
    Denied,
    Expired,
    InvalidResponse,
    Timeout,
    RateLimited,
    NotConfigured,
    InternalError
}

public sealed record PhoneAuthOutcome(PhoneAuthResultCode Code, string Message)
{
    public bool IsSuccess => Code == PhoneAuthResultCode.Success;
}

public sealed record StoredWindowsCredential(
    string Sid,
    string QualifiedUsername,
    string Domain,
    string Username,
    string Password);
