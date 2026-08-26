using Windows.Devices.Sensors;
using Windows.Foundation;

namespace PhoneUnlock.Agent;

/// <summary>
/// Bridges a Windows 11 human-presence sensor into the existing user-session
/// agent. Unsupported PCs simply produce no samples and retain phone/Bluetooth
/// presence behavior.
/// </summary>
internal sealed class HumanPresenceWatcher : IDisposable
{
    private readonly HumanPresenceSensor sensor;
    private readonly TypedEventHandler<HumanPresenceSensor, HumanPresenceSensorReadingChangedEventArgs> handler;
    private readonly Action<bool> report;

    private HumanPresenceWatcher(HumanPresenceSensor sensor, Action<bool> report)
    {
        this.sensor = sensor;
        this.report = report;
        handler = OnReadingChanged;
        sensor.ReadingChanged += handler;
        Report(sensor.GetCurrentReading());
    }

    public static HumanPresenceWatcher? TryStart(Action<bool> report)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return null;
        }

        try
        {
            var sensor = HumanPresenceSensor.GetDefault();
            return sensor is { IsPresenceSupported: true }
                ? new HumanPresenceWatcher(sensor, report)
                : null;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Presence sensing is optional and device-driver dependent.
            return null;
        }
    }

    private void OnReadingChanged(HumanPresenceSensor sender, HumanPresenceSensorReadingChangedEventArgs args) =>
        Report(args.Reading);

    private void Report(HumanPresenceSensorReading? reading)
    {
        switch (reading?.Presence)
        {
            case HumanPresence.Present:
                report(true);
                break;
            case HumanPresence.NotPresent:
                report(false);
                break;
        }
    }

    public void Dispose() => sensor.ReadingChanged -= handler;
}
