using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace NintendoProBridge;

internal sealed class ProControllerInput : IDisposable
{
    private const int StickRange = 99;
    private static readonly long InitialRepeatTicks = Stopwatch.Frequency / 2;
    private static readonly long RepeatTicks = Stopwatch.Frequency / 15;
    private static readonly long RumbleMinimumWriteTicks = Stopwatch.Frequency * 30 / 1000;
    private static readonly long RumbleRefreshTicks = Stopwatch.Frequency * 50 / 1000;

    private readonly PluginSettings settings;
    private readonly Action saveSettings;
    private readonly Hook<PadDevice.Delegates.Poll> pollHook;
    private readonly Hook<PadDevice.Delegates.SetVibration> vibrationHook;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task readerTask;
    private readonly object streamLock = new();
    private readonly object calibrationLock = new();
    private readonly long[] nextRepeat = new long[16];
    private FileStream? currentStream;
    private ControllerSnapshot snapshot = ControllerSnapshot.Disconnected;
    private string? lastError;
    private GamepadButtonsFlags previousButtons;
    private int desiredLeftMotor;
    private int desiredRightMotor;
    private long testRumbleUntil;
    private int packetNumber;
    private CalibrationMode calibrationMode;
    private CalibrationResult calibrationResult;
    private long centerCalibrationEnd;
    private long centerLeftX;
    private long centerLeftY;
    private long centerRightX;
    private long centerRightY;
    private int centerSamples;
    private RawStickState rangeMinimum = RawStickState.Maximum;
    private RawStickState rangeMaximum = RawStickState.Minimum;
    private bool disposed;

    public bool IsConnected => Volatile.Read(ref snapshot).Connected;
    public string? LastError => Volatile.Read(ref lastError);
    public CalibrationMode CurrentCalibrationMode
    {
        get { lock (calibrationLock) return calibrationMode; }
    }
    public CalibrationResult LastCalibrationResult
    {
        get { lock (calibrationLock) return calibrationResult; }
    }
    public int RangeDirectionsCaptured
    {
        get
        {
            lock (calibrationLock)
            {
                if (calibrationMode != CalibrationMode.Range) return 0;
                return CountCapturedDirections(rangeMinimum, rangeMaximum);
            }
        }
    }

    public unsafe ProControllerInput(IGameInteropProvider interop, PluginSettings settings, Action saveSettings)
    {
        this.settings = settings;
        this.saveSettings = saveSettings;
        pollHook = interop.HookFromAddress((nint)PadDevice.StaticVirtualTablePointer->Poll,
            (PadDevice.Delegates.Poll)PollDetour);
        vibrationHook = interop.HookFromAddress((nint)PadDevice.StaticVirtualTablePointer->SetVibration,
            (PadDevice.Delegates.SetVibration)VibrationDetour);
        pollHook.Enable();
        vibrationHook.Enable();
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

    private unsafe void VibrationDetour(PadDevice* device, int rightMotorSpeed, int leftMotorSpeed)
    {
        try
        {
            if (settings.Enabled && Volatile.Read(ref snapshot).Connected)
            {
                if (settings.EnableRumble)
                {
                    Volatile.Write(ref desiredRightMotor, ScaleMotorSpeed(rightMotorSpeed));
                    Volatile.Write(ref desiredLeftMotor, ScaleMotorSpeed(leftMotorSpeed));
                }
                else
                {
                    StopRumble();
                }
                return;
            }
        }
        catch
        {
            // Vibration must never make the game unstable. Fall back to the native handler.
        }
        vibrationHook.Original(device, rightMotorSpeed, leftMotorSpeed);
    }

    public void TestRumble()
    {
        if (!settings.Enabled || !settings.EnableRumble || !IsConnected) return;
        Volatile.Write(ref testRumbleUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2);
    }

    public void StartCenterCalibration()
    {
        if (!IsConnected) return;
        lock (calibrationLock)
        {
            calibrationMode = CalibrationMode.Center;
            calibrationResult = CalibrationResult.None;
            centerCalibrationEnd = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            centerLeftX = centerLeftY = centerRightX = centerRightY = 0;
            centerSamples = 0;
        }
    }

    public void StartRangeCalibration()
    {
        if (!IsConnected) return;
        lock (calibrationLock)
        {
            calibrationMode = CalibrationMode.Range;
            calibrationResult = CalibrationResult.None;
            rangeMinimum = RawStickState.Maximum;
            rangeMaximum = RawStickState.Minimum;
        }
    }

    public bool FinishRangeCalibration()
    {
        lock (calibrationLock)
        {
            if (calibrationMode != CalibrationMode.Range) return false;
            if (CountCapturedDirections(rangeMinimum, rangeMaximum) < 8)
            {
                calibrationResult = CalibrationResult.RangeIncomplete;
                return false;
            }

            settings.LeftStickCalibration.SetRange(rangeMinimum.LeftX, rangeMaximum.LeftX,
                rangeMinimum.LeftY, rangeMaximum.LeftY);
            settings.RightStickCalibration.SetRange(rangeMinimum.RightX, rangeMaximum.RightX,
                rangeMinimum.RightY, rangeMaximum.RightY);
            calibrationMode = CalibrationMode.None;
            calibrationResult = CalibrationResult.RangeComplete;
        }
        saveSettings();
        return true;
    }

    public void CancelCalibration()
    {
        lock (calibrationLock)
        {
            calibrationMode = CalibrationMode.None;
            calibrationResult = CalibrationResult.None;
        }
    }

    public void ResetCalibration()
    {
        lock (calibrationLock)
        {
            settings.LeftStickCalibration.Reset();
            settings.RightStickCalibration.Reset();
            calibrationMode = CalibrationMode.None;
            calibrationResult = CalibrationResult.Reset;
        }
        saveSettings();
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
                    Interlocked.Exchange(ref packetNumber, 0);
                    StopRumble();
                    if (device.IsUsb)
                        await TryInitializeUsbAsync(device.Stream, device.InputReportLength,
                            device.OutputReportLength, token);
                    await TryEnableVibrationAsync(device.Stream, device.OutputReportLength, token);
                    await Task.Delay(10, token);
                    await TryEnableFullInputReportsAsync(device.Stream, device.OutputReportLength, token);
                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var rumbleTask = RunRumbleLoopAsync(device.Stream, device.OutputReportLength,
                        connectionCancellation.Token);
                    var report = new byte[device.InputReportLength];
                    try
                    {
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
                    finally
                    {
                        connectionCancellation.Cancel();
                        try { await rumbleTask; }
                        catch (OperationCanceledException) { }
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
        StopRumble();
        lock (calibrationLock)
        {
            if (calibrationMode != CalibrationMode.None)
            {
                calibrationMode = CalibrationMode.None;
                calibrationResult = CalibrationResult.Disconnected;
            }
        }
    }

    private void StopRumble()
    {
        Volatile.Write(ref desiredLeftMotor, 0);
        Volatile.Write(ref desiredRightMotor, 0);
        Volatile.Write(ref testRumbleUntil, 0);
    }

    private static int ScaleMotorSpeed(int percent) =>
        (Math.Clamp(percent, 0, 100) * ushort.MaxValue + 50) / 100;

    private bool TryDecode(ReadOnlySpan<byte> report, out ControllerSnapshot state)
    {
        state = ControllerSnapshot.Disconnected;
        if (report.Length < 8) return false;
        switch (report[0])
        {
            case 0x30 when report.Length >= 12:
            {
                var right = report[3]; var shared = report[4]; var left = report[5];
                var raw = new RawStickState(
                    report[6] | ((report[7] & 0x0F) << 8),
                    (report[7] >> 4) | (report[8] << 4),
                    report[9] | ((report[10] & 0x0F) << 8),
                    (report[10] >> 4) | (report[11] << 4));
                UpdateCalibration(raw);
                state = CreateState(
                    Stick(raw.LeftX, settings.LeftStickCalibration.CenterX,
                        settings.LeftStickCalibration.MinimumX, settings.LeftStickCalibration.MaximumX),
                    Stick(raw.LeftY, settings.LeftStickCalibration.CenterY,
                        settings.LeftStickCalibration.MinimumY, settings.LeftStickCalibration.MaximumY),
                    Stick(raw.RightX, settings.RightStickCalibration.CenterX,
                        settings.RightStickCalibration.MinimumX, settings.RightStickCalibration.MaximumX),
                    Stick(raw.RightY, settings.RightStickCalibration.CenterY,
                        settings.RightStickCalibration.MinimumY, settings.RightStickCalibration.MaximumY),
                    right, shared, left);
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

    private static float Stick(int value, int center, int minimum, int maximum)
    {
        var positiveRange = Math.Max(maximum - center, 1);
        var negativeRange = Math.Max(center - minimum, 1);
        return Math.Clamp(value >= center ? (value - center) / (float)positiveRange :
            (value - center) / (float)negativeRange, -1f, 1f);
    }

    private void UpdateCalibration(RawStickState raw)
    {
        var save = false;
        lock (calibrationLock)
        {
            if (calibrationMode == CalibrationMode.Center)
            {
                centerLeftX += raw.LeftX;
                centerLeftY += raw.LeftY;
                centerRightX += raw.RightX;
                centerRightY += raw.RightY;
                centerSamples++;
                if (Stopwatch.GetTimestamp() >= centerCalibrationEnd && centerSamples > 0)
                {
                    settings.LeftStickCalibration.SetCenter(
                        (int)(centerLeftX / centerSamples), (int)(centerLeftY / centerSamples));
                    settings.RightStickCalibration.SetCenter(
                        (int)(centerRightX / centerSamples), (int)(centerRightY / centerSamples));
                    calibrationMode = CalibrationMode.None;
                    calibrationResult = CalibrationResult.CenterComplete;
                    save = true;
                }
            }
            else if (calibrationMode == CalibrationMode.Range)
            {
                rangeMinimum = RawStickState.Min(rangeMinimum, raw);
                rangeMaximum = RawStickState.Max(rangeMaximum, raw);
            }
        }
        if (save) saveSettings();
    }

    private int CountCapturedDirections(RawStickState minimum, RawStickState maximum)
    {
        const int requiredTravel = 512;
        var count = 0;
        if (minimum.LeftX <= settings.LeftStickCalibration.CenterX - requiredTravel) count++;
        if (maximum.LeftX >= settings.LeftStickCalibration.CenterX + requiredTravel) count++;
        if (minimum.LeftY <= settings.LeftStickCalibration.CenterY - requiredTravel) count++;
        if (maximum.LeftY >= settings.LeftStickCalibration.CenterY + requiredTravel) count++;
        if (minimum.RightX <= settings.RightStickCalibration.CenterX - requiredTravel) count++;
        if (maximum.RightX >= settings.RightStickCalibration.CenterX + requiredTravel) count++;
        if (minimum.RightY <= settings.RightStickCalibration.CenterY - requiredTravel) count++;
        if (maximum.RightY >= settings.RightStickCalibration.CenterY + requiredTravel) count++;
        return count;
    }

    private static float ApplyDeadzone(float value, float deadzone)
    {
        var magnitude = Math.Abs(value);
        if (magnitude <= deadzone) return 0;
        return Math.Clamp(MathF.Sign(value) * (magnitude - deadzone) / (1 - deadzone), -1f, 1f);
    }

    private static async Task TryInitializeUsbAsync(FileStream stream, int inputReportLength,
        int outputReportLength, CancellationToken token)
    {
        try
        {
            foreach (var commandId in new byte[] { 0x01, 0x02, 0x03, 0x02, 0x04 })
            {
                var command = new byte[outputReportLength];
                command[0] = 0x80; command[1] = commandId;
                await stream.WriteAsync(command, token);
                var response = new byte[inputReportLength];
                await stream.ReadAtLeastAsync(response, 1, throwOnEndOfStream: false, token);
            }
        }
        catch (IOException) { }
    }

    private async Task TryEnableFullInputReportsAsync(FileStream stream, int outputReportLength,
        CancellationToken token)
    {
        try
        {
            await stream.WriteAsync(CreateSubcommand(outputReportLength, 0x03, 0x30), token);
        }
        catch (IOException) { }
    }

    private async Task TryEnableVibrationAsync(FileStream stream, int outputReportLength,
        CancellationToken token)
    {
        try { await stream.WriteAsync(CreateSubcommand(outputReportLength, 0x48, 0x01), token); }
        catch (IOException) { }
    }

    private byte[] CreateSubcommand(int outputReportLength, byte subcommand, byte argument)
    {
        if (outputReportLength < 12) throw new IOException("HID output report is too short.");
        var command = new byte[outputReportLength];
        command[0] = 0x01;
        command[1] = NextPacketNumber();
        SwitchRumble.SetNeutral(command.AsSpan(2, 4));
        SwitchRumble.SetNeutral(command.AsSpan(6, 4));
        command[10] = subcommand;
        command[11] = argument;
        return command;
    }

    private async Task RunRumbleLoopAsync(FileStream stream, int outputReportLength, CancellationToken token)
    {
        var lastLeft = -1;
        var lastRight = -1;
        var lastWrite = 0L;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                var testing = now < Volatile.Read(ref testRumbleUntil);
                var enabled = settings.Enabled && settings.EnableRumble;
                var left = enabled ? (testing ? 32768 : Volatile.Read(ref desiredLeftMotor)) : 0;
                var right = enabled ? (testing ? 32768 : Volatile.Read(ref desiredRightMotor)) : 0;
                var active = left != 0 || right != 0;
                var changed = left != lastLeft || right != lastRight;
                var elapsed = now - lastWrite;
                var stopImmediately = !active && (lastLeft > 0 || lastRight > 0);
                if ((changed && (lastWrite == 0 || elapsed >= RumbleMinimumWriteTicks)) ||
                    stopImmediately || (active && elapsed >= RumbleRefreshTicks))
                {
                    await WriteRumbleAsync(stream, outputReportLength, left, right, token);
                    lastLeft = left;
                    lastRight = right;
                    lastWrite = Stopwatch.GetTimestamp();
                }
                await Task.Delay(10, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(100);
                await WriteRumbleAsync(stream, outputReportLength, 0, 0, stopTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task WriteRumbleAsync(FileStream stream, int outputReportLength, int leftMotor, int rightMotor,
        CancellationToken token)
    {
        if (outputReportLength < 10) throw new IOException("HID output report is too short.");
        var report = new byte[outputReportLength];
        report[0] = 0x10;
        report[1] = NextPacketNumber();
        SwitchRumble.Encode(report.AsSpan(2, 4), (ushort)leftMotor, (ushort)rightMotor);
        report.AsSpan(2, 4).CopyTo(report.AsSpan(6, 4));
        await stream.WriteAsync(report, token);
    }

    private byte NextPacketNumber() => (byte)((Interlocked.Increment(ref packetNumber) - 1) & 0x0F);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        vibrationHook.Disable();
        pollHook.Disable();
        StopRumble();
        cancellation.Cancel();
        try { readerTask.Wait(1500); }
        catch (AggregateException) { }
        if (!readerTask.IsCompleted)
        {
            lock (streamLock) currentStream?.Close();
            try { readerTask.Wait(500); }
            catch (AggregateException) { }
        }
        vibrationHook.Dispose();
        pollHook.Dispose();
        cancellation.Dispose();
    }

    private sealed record ControllerSnapshot(bool Connected, float LeftX, float LeftY, float RightX, float RightY,
        GamepadButtonsFlags Buttons)
    {
        public static readonly ControllerSnapshot Disconnected = new(false, 0, 0, 0, 0, GamepadButtonsFlags.None);
    }

    private sealed record RawStickState(int LeftX, int LeftY, int RightX, int RightY)
    {
        public static readonly RawStickState Maximum = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        public static readonly RawStickState Minimum = new(int.MinValue, int.MinValue, int.MinValue, int.MinValue);
        public static RawStickState Min(RawStickState a, RawStickState b) => new(
            Math.Min(a.LeftX, b.LeftX), Math.Min(a.LeftY, b.LeftY),
            Math.Min(a.RightX, b.RightX), Math.Min(a.RightY, b.RightY));
        public static RawStickState Max(RawStickState a, RawStickState b) => new(
            Math.Max(a.LeftX, b.LeftX), Math.Max(a.LeftY, b.LeftY),
            Math.Max(a.RightX, b.RightX), Math.Max(a.RightY, b.RightY));
    }
}

internal enum CalibrationMode { None, Center, Range }
internal enum CalibrationResult { None, CenterComplete, RangeComplete, RangeIncomplete, Reset, Disconnected }
