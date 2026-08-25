using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using PhoneUnlock.Agent;

if (!OperatingSystem.IsWindows())
{
    return;
}

while (true)
{
    try
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            "PhoneUnlock.Agent",
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await pipe.ConnectAsync(8_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("READY");
        using var rssiWatcher = new BluetoothRssiWatcher((phoneId, rssi) =>
        {
            try
            {
                lock (writer)
                {
                    if (pipe.IsConnected)
                    {
                        writer.WriteLine($"RSSI|{phoneId}|{rssi}");
                    }
                }
            }
            catch (IOException)
            {
                // The service will recreate the pipe after a disconnect.
            }
        });
        try
        {
            rssiWatcher.Start();
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Bluetooth is optional. Phone heartbeat and presence sensors remain available.
        }

        while (pipe.IsConnected)
        {
            var command = await reader.ReadLineAsync();
            if (command is null) break;
            if (string.Equals(command, "LOCK", StringComparison.Ordinal))
            {
                LockWorkStation();
            }
        }
    }
    catch (OperationCanceledException)
    {
        return;
    }
    catch (IOException)
    {
        // The service may be restarting or the feature may be disabled.
    }
    await Task.Delay(TimeSpan.FromSeconds(5));
}

[DllImport("user32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool LockWorkStation();
