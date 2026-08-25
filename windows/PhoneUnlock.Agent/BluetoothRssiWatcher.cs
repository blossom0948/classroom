using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PhoneUnlock.Agent;

public sealed class BluetoothRssiWatcher(Action<string, short> received) : IDisposable
{
    private const byte ServiceDataType = 0x16;
    private const byte ServiceUuidLow = 0xA0;
    private const byte ServiceUuidHigh = 0xF2;
    private readonly BluetoothLEAdvertisementWatcher watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active
    };
    private int stopped;

    public void Start()
    {
        watcher.Received += OnReceived;
        watcher.Start();
    }

    private void OnReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (Volatile.Read(ref stopped) == 1)
        {
            return;
        }

        foreach (var section in args.Advertisement.DataSections)
        {
            if (section.DataType != ServiceDataType)
            {
                continue;
            }

            try
            {
                using var reader = DataReader.FromBuffer(section.Data);
                if (reader.UnconsumedBufferLength < 18)
                {
                    continue;
                }

                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                if (bytes[0] != ServiceUuidLow || bytes[1] != ServiceUuidHigh)
                {
                    continue;
                }

                var phoneId = ConvertBeaconId(bytes.AsSpan(2));
                if (phoneId is not null)
                {
                    received(phoneId, args.RawSignalStrengthInDBm);
                }
            }
            catch (Exception) when (Volatile.Read(ref stopped) == 0)
            {
                // Advertisement sections are untrusted and may be malformed.
            }
        }
    }

    private static string? ConvertBeaconId(ReadOnlySpan<byte> value)
    {
        if (value.Length < 16)
        {
            return null;
        }

        Span<char> hex = stackalloc char[32];
        for (var index = 0; index < 16; index++)
        {
            var offset = index * 2;
            hex[offset] = GetHex(value[index] >> 4);
            hex[offset + 1] = GetHex(value[index] & 0x0F);
        }

        return Guid.TryParseExact(hex, "N", out var id) ? id.ToString() : null;
    }

    private static char GetHex(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1)
        {
            return;
        }

        watcher.Received -= OnReceived;
        watcher.Stop();
    }
}
