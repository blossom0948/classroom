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
            await writer.WriteLineAsync($"SESSION|{(tray.IsWorkstationLocked ? "LOCKED" : "UNLOCKED")}");
            void PublishSessionState(bool locked)
            {
                try
                {
                    lock (writer)
                    {
                        if (pipe.IsConnected)
                        {
                            writer.WriteLine($"SESSION|{(locked ? "LOCKED" : "UNLOCKED")}");
                        }
                    }
                }
                catch (IOException)
                {
                    // The next pipe connection publishes a fresh state.
                }
            }
            tray.WorkstationLockStateChanged += PublishSessionState;
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

            try
            {
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
                    else if (command.StartsWith("DECK|", StringComparison.Ordinal))
                    {
                        ExecuteDeckAction(command["DECK|".Length..]);
                    }
                }
            }
            finally
            {
                tray.WorkstationLockStateChanged -= PublishSessionState;
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

static void ExecuteDeckAction(string action)
{
    switch (action)
    {
        case "MEDIA_PLAY_PAUSE": NativeMethods.PressKey(0xB3); break;
        case "MEDIA_NEXT": NativeMethods.PressKey(0xB0); break;
        case "MEDIA_PREVIOUS": NativeMethods.PressKey(0xB1); break;
        case "VOLUME_UP": NativeMethods.PressKey(0xAF); break;
        case "VOLUME_DOWN": NativeMethods.PressKey(0xAE); break;
        case "VOLUME_MUTE": NativeMethods.PressKey(0xAD); break;
        case "SCREENSHOT": NativeMethods.PressKey(0x2C); break;
        case "SHOW_DESKTOP": NativeMethods.PressChord(0x5B, 0x44); break;
        case "OPEN_EXPLORER": StartTarget("explorer.exe"); break;
        case "OPEN_BROWSER": StartTarget("https://www.google.com"); break;
        case "OPEN_SPOTIFY": StartTarget("spotify:"); break;
        case "OPEN_STEAM": StartTarget("steam:"); break;
    }
}

static void StartTarget(string target)
{
    try
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
    catch (Exception) when (OperatingSystem.IsWindows())
    {
        // Optional applications may not be installed.
    }
}

static class NativeMethods
{
    private const uint KeyEventKeyUp = 0x0002;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public static void PressKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    public static void PressChord(byte modifier, byte key)
    {
        keybd_event(modifier, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(modifier, 0, KeyEventKeyUp, UIntPtr.Zero);
    }
}
