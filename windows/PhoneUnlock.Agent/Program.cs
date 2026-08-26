using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;
using PhoneUnlock.Agent;

if (!OperatingSystem.IsWindows())
{
    return;
}

using var singleInstance = new Mutex(true, "Local\\PhoneUnlock.Agent", out var ownsInstance);
if (!ownsInstance)
{
    return;
}

using var tray = new AgentTrayContext();
var agentTask = RunAgentAsync(tray, tray.StoppingToken);
Application.Run(tray);
try
{
    await agentTask;
}
catch (OperationCanceledException) when (tray.StoppingToken.IsCancellationRequested)
{
}

static async Task RunAgentAsync(AgentTrayContext tray, CancellationToken stoppingToken)
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
            using var humanPresenceWatcher = HumanPresenceWatcher.TryStart(present =>
            {
                try
                {
                    lock (writer)
                    {
                        if (pipe.IsConnected)
                        {
                            writer.WriteLine($"PRESENCE|{(present ? "PRESENT" : "ABSENT")}");
                        }
                    }
                }
                catch (IOException)
                {
                    // The service will request a fresh sample after reconnecting.
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
                else if (command.StartsWith("NOTICE|", StringComparison.Ordinal))
                {
                    ShowNotice(tray, command);
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

static void ShowNotice(AgentTrayContext tray, string command)
{
    var parts = command.Split('|', 3);
    if (parts.Length != 3)
    {
        return;
    }

    try
    {
        var title = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        var message = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
        tray.ShowNotice(title, message);
    }
    catch (FormatException)
    {
        // Ignore malformed service messages instead of showing arbitrary UI.
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
