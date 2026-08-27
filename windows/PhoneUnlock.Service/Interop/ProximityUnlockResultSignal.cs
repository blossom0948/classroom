namespace PhoneUnlock.Service.Interop;

/// <summary>
/// Receives a one-shot signal from the credential provider only after Windows
/// reports that the serialized credentials were accepted. This prevents an
/// arrival/presence transition from being presented as a successful unlock.
/// </summary>
public sealed class ProximityUnlockResultSignal : IDisposable
{
    public const string TrustedPhoneEventName = @"Global\PhoneUnlock.ProximityUnlockSucceeded.TrustedPhone";
    public const string RoomSensorEventName = @"Global\PhoneUnlock.ProximityUnlockSucceeded.RoomSensor";
    public const string PhoneBiometricEventName = @"Global\PhoneUnlock.ProximityUnlockSucceeded.PhoneBiometric";

    private readonly EventWaitHandle? trustedPhoneHandle = CreateHandle(TrustedPhoneEventName);
    private readonly EventWaitHandle? roomSensorHandle = CreateHandle(RoomSensorEventName);
    private readonly EventWaitHandle? phoneBiometricHandle = CreateHandle(PhoneBiometricEventName);

    private static EventWaitHandle? CreateHandle(string name)
    {
        try
        {
            return new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: name,
                out _);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool TryConsume(out ProximityUnlockSource source)
    {
        source = default;
        try
        {
            if (trustedPhoneHandle?.WaitOne(0) == true)
            {
                source = ProximityUnlockSource.TrustedPhone;
                return true;
            }

            if (roomSensorHandle?.WaitOne(0) == true)
            {
                source = ProximityUnlockSource.RoomSensor;
                return true;
            }

            if (phoneBiometricHandle?.WaitOne(0) == true)
            {
                source = ProximityUnlockSource.PhoneBiometric;
                return true;
            }
        }
        catch (ObjectDisposedException)
        {
        }

        return false;
    }

    public void Dispose()
    {
        trustedPhoneHandle?.Dispose();
        roomSensorHandle?.Dispose();
        phoneBiometricHandle?.Dispose();
    }
}
