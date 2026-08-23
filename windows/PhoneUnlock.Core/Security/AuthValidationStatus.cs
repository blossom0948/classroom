namespace PhoneUnlock.Core.Security;

public enum AuthValidationStatus
{
    Success,
    UnsupportedProtocol,
    WrongMessageType,
    UnknownRequest,
    Expired,
    RequestMismatch,
    InvalidPublicKeyOrSignature,
    Replayed
}
