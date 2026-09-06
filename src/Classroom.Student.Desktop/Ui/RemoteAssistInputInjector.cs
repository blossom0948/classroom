using System.Runtime.InteropServices;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Desktop.Ui;

/// <summary>
/// Applies only bounded pointer and keyboard events to the current interactive
/// Windows desktop. It intentionally does not start processes, cross the UAC
/// secure desktop, or handle Windows/meta and Alt shortcuts.
/// </summary>
internal static class RemoteAssistInputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint KeyUp = 0x0002;
    private static readonly object inputGate = new();
    private static readonly HashSet<ushort> pressedKeys = [];
    private static readonly HashSet<RemoteMouseButton> pressedButtons = [];

    public static bool TryApply(RemoteAssistInput input)
    {
        try
        {
            return input.Kind switch
            {
                RemoteAssistInputKind.PointerMove => MovePointer(input.X!.Value, input.Y!.Value),
                RemoteAssistInputKind.PointerButton => ApplyPointerButton(input),
                RemoteAssistInputKind.PointerWheel => ApplyPointerWheel(input),
                RemoteAssistInputKind.Key => ApplyKey(input),
                _ => false
            };
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            return false;
        }
    }

    private static bool ApplyPointerButton(RemoteAssistInput input)
    {
        if (!MovePointer(input.X!.Value, input.Y!.Value)
            || input.Button is not { } button
            || input.IsDown is not { } isDown)
        {
            return false;
        }

        var flags = (button, isDown) switch
        {
            (RemoteMouseButton.Left, true) => MouseLeftDown,
            (RemoteMouseButton.Left, false) => MouseLeftUp,
            (RemoteMouseButton.Middle, true) => MouseMiddleDown,
            (RemoteMouseButton.Middle, false) => MouseMiddleUp,
            (RemoteMouseButton.Right, true) => MouseRightDown,
            (RemoteMouseButton.Right, false) => MouseRightUp,
            _ => 0u
        };
        if (flags == 0 || !SendMouse(flags, 0)) return false;

        lock (inputGate)
        {
            if (isDown) pressedButtons.Add(button);
            else pressedButtons.Remove(button);
        }

        return true;
    }

    private static bool ApplyPointerWheel(RemoteAssistInput input) =>
        MovePointer(input.X!.Value, input.Y!.Value)
        && input.WheelDelta is { } wheelDelta
        && SendMouse(MouseWheel, unchecked((uint)wheelDelta));

    private static bool ApplyKey(RemoteAssistInput input)
    {
        if (input.KeyCode is null
            || input.IsDown is not { } isDown
            || !TryResolveVirtualKey(input.KeyCode, out var virtualKey)) return false;

        var native = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = isDown ? 0u : KeyUp }
            }
        };
        if (SendInput(1, [native], Marshal.SizeOf<Input>()) != 1) return false;

        lock (inputGate)
        {
            if (isDown) pressedKeys.Add(virtualKey);
            else pressedKeys.Remove(virtualKey);
        }

        return true;
    }

    private static bool MovePointer(double normalizedX, double normalizedY)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (bounds.Width < 1 || bounds.Height < 1) return false;
        var x = bounds.Left + (int)Math.Round(Math.Clamp(normalizedX, 0, 1) * (bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(Math.Clamp(normalizedY, 0, 1) * (bounds.Height - 1));
        return SetCursorPos(x, y);
    }

    private static bool SendMouse(uint flags, uint mouseData)
    {
        var native = new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { MouseData = mouseData, Flags = flags } }
        };
        return SendInput(1, [native], Marshal.SizeOf<Input>()) == 1;
    }

    public static void ReleaseAll()
    {
        Input[] releases;
        lock (inputGate)
        {
            releases = pressedKeys.Select(virtualKey => new Input
                {
                    Type = InputKeyboard,
                    Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = KeyUp } }
                })
                .Concat(pressedButtons.Select(button => new Input
                {
                    Type = InputMouse,
                    Union = new InputUnion { Mouse = new MouseInput { Flags = MouseButtonFlags(button) } }
                }))
                .ToArray();
            pressedKeys.Clear();
            pressedButtons.Clear();
        }

        if (releases.Length > 0) SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
    }

    private static uint MouseButtonFlags(RemoteMouseButton button) => button switch
    {
        RemoteMouseButton.Left => MouseLeftUp,
        RemoteMouseButton.Middle => MouseMiddleUp,
        RemoteMouseButton.Right => MouseRightUp,
        _ => 0u
    };

    private static bool TryResolveVirtualKey(string code, out ushort virtualKey)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal)
            && code[3] is >= 'A' and <= 'Z')
        {
            virtualKey = code[3];
            return true;
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal)
            && code[5] is >= '0' and <= '9')
        {
            virtualKey = code[5];
            return true;
        }

        return FixedVirtualKeys.TryGetValue(code, out virtualKey);
    }

    private static readonly IReadOnlyDictionary<string, ushort> FixedVirtualKeys =
        new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D,
            ["ShiftLeft"] = 0x10, ["ShiftRight"] = 0x10,
            ["ControlLeft"] = 0x11, ["ControlRight"] = 0x11,
            ["CapsLock"] = 0x14, ["Escape"] = 0x1B, ["Space"] = 0x20,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23,
            ["Home"] = 0x24, ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26,
            ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
            ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Semicolon"] = 0xBA,
            ["Equal"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD,
            ["Period"] = 0xBE, ["Slash"] = 0xBF, ["Backquote"] = 0xC0,
            ["BracketLeft"] = 0xDB, ["Backslash"] = 0xDC, ["BracketRight"] = 0xDD,
            ["Quote"] = 0xDE,
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
            ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
            ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B
        };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, [MarshalAs(UnmanagedType.LPArray), In] Input[] inputs, int sizeOfInput);

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nint ExtraInfo;
    }
}
