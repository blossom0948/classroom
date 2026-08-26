using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;
using PhoneUnlock.Agent;

if (!OperatingSystem.IsWindows())
{
    return;
}

using var tray = new AgentTrayContext();
var agentTask = RunAgentAsync(tray.StoppingToken);
Application.Run(tray);
try
{
    await agentTask;
}
catch (OperationCanceledException) when (tray.StoppingToken.IsCancellationRequested)
{
}

static async Task RunAgentAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                "PhoneUnlock.Agent",
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync(8_000, stoppingToken);
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

            while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                var command = await reader.ReadLineAsync(stoppingToken);
                if (command is null) break;
                if (string.Equals(command, "LOCK", StringComparison.Ordinal))
                {
                    LockWorkStation();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException)
        {
            // The service may be restarting or the feature may be disabled.
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
    }
}

static void LockWorkStation()
{
    _ = NativeMethods.LockWorkStation();
}

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool LockWorkStation();
}
