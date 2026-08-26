namespace PhoneUnlock.Service.Interop;

/// <summary>
/// Signals the Credential Provider on the secure desktop that an explicitly
/// enabled trusted-phone presence transition occurred. This is opt-in; the
/// event never contains credentials or an unlock token.
/// </summary>
public sealed class ProximityUnlockSignal : IDisposable
{
    public const string EventName = @"Global\PhoneUnlock.ProximityUnlock";
    public const string TrustedPhoneEventName = @"Global\PhoneUnlock.ProximityUnlock.TrustedPhone";
    public const string RoomSensorEventName = @"Global\PhoneUnlock.ProximityUnlock.RoomSensor";

    private readonly EventWaitHandle? handle = CreateHandle(EventName);
    private readonly EventWaitHandle? trustedPhoneHandle = CreateHandle(TrustedPhoneEventName);
    private readonly EventWaitHandle? roomSensorHandle = CreateHandle(RoomSensorEventName);

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

    public void Signal(ProximityUnlockSource source)
    {
        try
        {
            switch (source)
            {
                case ProximityUnlockSource.TrustedPhone:
                    trustedPhoneHandle?.Set();
                    break;
                case ProximityUnlockSource.RoomSensor:
                    roomSensorHandle?.Set();
                    break;
            }
            // Keep the original event for older credential providers during an upgrade.
            handle?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Reset()
    {
        try
        {
            handle?.Reset();
            trustedPhoneHandle?.Reset();
            roomSensorHandle?.Reset();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        handle?.Dispose();
        trustedPhoneHandle?.Dispose();
        roomSensorHandle?.Dispose();
    }
}

public enum ProximityUnlockSource
{
    TrustedPhone = 1,
    RoomSensor = 2,
}
