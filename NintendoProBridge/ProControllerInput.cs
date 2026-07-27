using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace NintendoProBridge;

internal sealed class ProControllerInput : IDisposable
{
    private const int StickRange = 1000;
    private static readonly long InitialRepeatTicks = Stopwatch.Frequency / 2;
    private static readonly long RepeatTicks = Stopwatch.Frequency / 15;

    private readonly PluginSettings settings;
    private readonly Hook<PadDevice.Delegates.Poll> pollHook;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task readerTask;
    private readonly object streamLock = new();
    private readonly long[] nextRepeat = new long[16];
    private FileStream? currentStream;
    private ControllerSnapshot snapshot = ControllerSnapshot.Disconnected;
    private string? lastError;
    private GamepadButtonsFlags previousButtons;
    private bool disposed;

    public bool IsConnected => Volatile.Read(ref snapshot).Connected;
    public string? LastError => Volatile.Read(ref lastError);

    public unsafe ProControllerInput(IGameInteropProvider interop, PluginSettings settings)
    {
        this.settings = settings;
        pollHook = interop.HookFromAddress((nint)PadDevice.StaticVirtualTablePointer->Poll,
            (PadDevice.Delegates.Poll)PollDetour);
        pollHook.Enable();
        readerTask = Task.Run(() => ReadLoopAsync(cancellation.Token));
    }

    private unsafe nint PollDetour(PadDevice* device)
    {
        try
        {
            var state = Volatile.Read(ref snapshot);
            if (settings.Enabled && state.Connected)
            {
                // Do not call the native poll here. The official Pro Controller (and Steam Input)
                // can produce navigation side effects inside Poll itself, so overwriting its output
                // after the call is already too late. Suppress it for FFXIV only and provide the
                // translated state directly.
                WriteGameState(ref device->GamepadInputData, state);
                return 0;
            }
        }
        catch
        {
            // Input hooks must never take the game down. Fall back to the game's original poll.
        }
        return pollHook.Original(device);
    }

    private void WriteGameState(ref GamepadInputData input, ControllerSnapshot state)
    {
        var buttons = state.Buttons;
        var pressed = buttons & ~previousButtons;
        var released = previousButtons & ~buttons;
        var repeat = pressed;
        var now = Stopwatch.GetTimestamp();
        for (var bit = 0; bit < 16; bit++)
        {
            var mask = (GamepadButtonsFlags)(1 << bit);
            if ((buttons & mask) == 0)
            {
                nextRepeat[bit] = 0;
                continue;
            }
            if ((pressed & mask) != 0)
                nextRepeat[bit] = now + InitialRepeatTicks;
            else if (nextRepeat[bit] != 0 && now >= nextRepeat[bit])
            {
                repeat |= mask;
                nextRepeat[bit] = now + RepeatTicks;
            }
        }
        previousButtons = buttons;

        input = default;
        input.LeftStickX = (int)MathF.Round(state.LeftX * StickRange);
        input.LeftStickY = (int)MathF.Round(state.LeftY * StickRange);
        input.RightStickX = (int)MathF.Round(state.RightX * StickRange);
        input.RightStickY = (int)MathF.Round(state.RightY * StickRange);
        input.Buttons = buttons;
        input.ButtonsPressed = pressed;
        input.ButtonsReleased = released;
        input.ButtonsRepeat = repeat;

        input.Cross = Down(buttons, GamepadButtonsFlags.Cross);
        input.Circle = Down(buttons, GamepadButtonsFlags.Circle);
        input.Square = Down(buttons, GamepadButtonsFlags.Square);
        input.Triangle = Down(buttons, GamepadButtonsFlags.Triangle);
        input.L1 = Down(buttons, GamepadButtonsFlags.L1);
        input.R1 = Down(buttons, GamepadButtonsFlags.R1);
        input.L2 = Down(buttons, GamepadButtonsFlags.L2);
        input.R2 = Down(buttons, GamepadButtonsFlags.R2);
        input.Start = Down(buttons, GamepadButtonsFlags.Start);
        input.Select = Down(buttons, GamepadButtonsFlags.Select);
        input.L3 = Down(buttons, GamepadButtonsFlags.L3);
        input.R3 = Down(buttons, GamepadButtonsFlags.R3);
        input.DPadLeft = Down(buttons, GamepadButtonsFlags.DPadLeft);
        input.DPadRight = Down(buttons, GamepadButtonsFlags.DPadRight);
        input.DPadUp = Down(buttons, GamepadButtonsFlags.DPadUp);
        input.DPadDown = Down(buttons, GamepadButtonsFlags.DPadDown);
        input.LeftStickLeft = MathF.Max(-state.LeftX, 0);
        input.LeftStickRight = MathF.Max(state.LeftX, 0);
        input.LeftStickUp = MathF.Max(state.LeftY, 0);
        input.LeftStickDown = MathF.Max(-state.LeftY, 0);
        input.RightStickLeft = MathF.Max(-state.RightX, 0);
        input.RightStickRight = MathF.Max(state.RightX, 0);
        input.RightStickUp = MathF.Max(state.RightY, 0);
        input.RightStickDown = MathF.Max(-state.RightY, 0);
    }

    private static float Down(GamepadButtonsFlags buttons, GamepadButtonsFlags button) =>
        (buttons & button) != 0 ? 1f : 0f;

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var device = NativeHid.TryOpenNintendoPro();
                if (device is null)
                {
                    SetDisconnected(null);
                    await Task.Delay(500, token);
                    continue;
                }

                await using (device.Stream)
                {
                    lock (streamLock) currentStream = device.Stream;
                    Volatile.Write(ref lastError, null);
                    if (device.IsUsb) await TryInitializeUsbAsync(device.Stream, token);
                    await TryEnableFullInputReportsAsync(device.Stream, token);
                    var report = new byte[64];
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var count = await device.Stream.ReadAsync(report, token);
                            if (count == 0) break;
                            if (count > 0 && TryDecode(report.AsSpan(0, count), out var decoded))
                                Volatile.Write(ref snapshot, decoded);
                        }
                        catch (IOException) { break; }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetDisconnected(ex.GetType().Name);
            }
            finally
            {
                lock (streamLock) currentStream = null;
                if (!token.IsCancellationRequested && !Volatile.Read(ref snapshot).Connected)
                    Volatile.Write(ref lastError, "read-failed");
            }

            if (!token.IsCancellationRequested)
            {
                SetDisconnected(lastError);
                try { await Task.Delay(350, token); }
                catch (OperationCanceledException) { break; }
            }
        }
        SetDisconnected(null);
    }

    private void SetDisconnected(string? error)
    {
        Volatile.Write(ref snapshot, ControllerSnapshot.Disconnected);
        Volatile.Write(ref lastError, error);
        previousButtons = 0;
        Array.Clear(nextRepeat);
    }

    private bool TryDecode(ReadOnlySpan<byte> report, out ControllerSnapshot state)
    {
        state = ControllerSnapshot.Disconnected;
        if (report.Length < 8) return false;
        switch (report[0])
        {
            case 0x30 when report.Length >= 12:
            {
                var right = report[3]; var shared = report[4]; var left = report[5];
                state = CreateState(
                    Stick(report[6] | ((report[7] & 0x0F) << 8)),
                    Stick((report[7] >> 4) | (report[8] << 4)),
                    Stick(report[9] | ((report[10] & 0x0F) << 8)),
                    Stick((report[10] >> 4) | (report[11] << 4)), right, shared, left);
                return true;
            }
            case 0x3F:
            {
                var right = report[1]; var shared = report[2]; var left = report[3];
                state = CreateState((report[4] - 128) / 127f, -(report[5] - 128) / 127f,
                    (report[6] - 128) / 127f, -(report[7] - 128) / 127f, right, shared, left);
                return true;
            }
            default:
                return false;
        }
    }

    private ControllerSnapshot CreateState(float lx, float ly, float rx, float ry, byte right, byte shared, byte left)
    {
        var buttons = GamepadButtonsFlags.None;
        Add(ref buttons, (right & 0x08) != 0, settings.SwapAb ? GamepadButtonsFlags.Circle : GamepadButtonsFlags.Cross);
        Add(ref buttons, (right & 0x04) != 0, settings.SwapAb ? GamepadButtonsFlags.Cross : GamepadButtonsFlags.Circle);
        Add(ref buttons, (right & 0x02) != 0, settings.SwapXy ? GamepadButtonsFlags.Triangle : GamepadButtonsFlags.Square);
        Add(ref buttons, (right & 0x01) != 0, settings.SwapXy ? GamepadButtonsFlags.Square : GamepadButtonsFlags.Triangle);
        Add(ref buttons, (right & 0x40) != 0, GamepadButtonsFlags.R1);
        Add(ref buttons, (right & 0x80) != 0, GamepadButtonsFlags.R2);
        Add(ref buttons, (left & 0x40) != 0, GamepadButtonsFlags.L1);
        Add(ref buttons, (left & 0x80) != 0, GamepadButtonsFlags.L2);
        Add(ref buttons, (shared & 0x02) != 0, GamepadButtonsFlags.Start);
        Add(ref buttons, (shared & 0x01) != 0, GamepadButtonsFlags.Select);
        Add(ref buttons, (shared & 0x08) != 0, GamepadButtonsFlags.L3);
        Add(ref buttons, (shared & 0x04) != 0, GamepadButtonsFlags.R3);
        Add(ref buttons, (left & 0x02) != 0, GamepadButtonsFlags.DPadUp);
        Add(ref buttons, (left & 0x01) != 0, GamepadButtonsFlags.DPadDown);
        Add(ref buttons, (left & 0x08) != 0, GamepadButtonsFlags.DPadLeft);
        Add(ref buttons, (left & 0x04) != 0, GamepadButtonsFlags.DPadRight);
        return new ControllerSnapshot(true,
            ApplyDeadzone(lx, settings.LeftDeadzone), ApplyDeadzone(ly, settings.LeftDeadzone),
            ApplyDeadzone(rx, settings.RightDeadzone), ApplyDeadzone(ry, settings.RightDeadzone), buttons);
    }

    private static void Add(ref GamepadButtonsFlags buttons, bool pressed, GamepadButtonsFlags button)
    {
        if (pressed) buttons |= button;
    }

    private static float Stick(int value)
    {
        const float center = 2048f, minimum = 500f, maximum = 3500f;
        return Math.Clamp(value >= center ? (value - center) / (maximum - center) :
            (value - center) / (center - minimum), -1f, 1f);
    }

    private static float ApplyDeadzone(float value, float deadzone)
    {
        var magnitude = Math.Abs(value);
        if (magnitude <= deadzone) return 0;
        return Math.Clamp(MathF.Sign(value) * (magnitude - deadzone) / (1 - deadzone), -1f, 1f);
    }

    private static async Task TryInitializeUsbAsync(FileStream stream, CancellationToken token)
    {
        try
        {
            foreach (var commandId in new byte[] { 0x01, 0x02, 0x03, 0x02, 0x04 })
            {
                var command = new byte[64];
                command[0] = 0x80; command[1] = commandId;
                await stream.WriteAsync(command, token);
                var response = new byte[64];
                await stream.ReadAtLeastAsync(response, 1, throwOnEndOfStream: false, token);
            }
        }
        catch (IOException) { }
    }

    private static async Task TryEnableFullInputReportsAsync(FileStream stream, CancellationToken token)
    {
        try
        {
            var command = new byte[64];
            command[0] = 0x01;
            command[2] = 0x00; command[3] = 0x01; command[4] = 0x40; command[5] = 0x40;
            command[6] = 0x00; command[7] = 0x01; command[8] = 0x40; command[9] = 0x40;
            command[10] = 0x03; command[11] = 0x30;
            await stream.WriteAsync(command, token);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pollHook.Disable();
        cancellation.Cancel();
        lock (streamLock)
        {
            currentStream?.Close();
        }
        try { readerTask.Wait(1500); }
        catch (AggregateException) { }
        pollHook.Dispose();
        cancellation.Dispose();
    }

    private sealed record ControllerSnapshot(bool Connected, float LeftX, float LeftY, float RightX, float RightY,
        GamepadButtonsFlags Buttons)
    {
        public static readonly ControllerSnapshot Disconnected = new(false, 0, 0, 0, 0, GamepadButtonsFlags.None);
    }
}
