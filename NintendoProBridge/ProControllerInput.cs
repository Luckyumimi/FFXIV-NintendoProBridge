using System.ComponentModel;
using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace NintendoProBridge;

internal sealed class ProControllerInput : IDisposable
{
    private const int StickRange = 99;
    private const int Switch2MaximumRumbleAmplitude = 29000;
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
    private NativeHidDeviceInfo[] availableDevices = [];
    private ControllerSnapshot snapshot = ControllerSnapshot.Disconnected;
    private volatile ControllerKind activeControllerKind;
    private string? lastError;
    private GamepadButtonsFlags previousButtons;
    private int desiredLeftMotor;
    private int desiredRightMotor;
    private long testRumbleUntil;
    private int packetNumber;
    private int rumbleSequence;
    private CalibrationMode calibrationMode;
    private CalibrationResult calibrationResult;
    private long centerCalibrationEnd;
    private long centerLeftX;
    private long centerLeftY;
    private long centerRightX;
    private long centerRightY;
    private int centerSamples;
    private readonly int[] leftRangeRadii = new int[8];
    private readonly int[] rightRangeRadii = new int[8];
    private bool disposed;

    public bool IsConnected => Volatile.Read(ref snapshot).Connected;
    public ControllerKind ActiveControllerKind => activeControllerKind;
    public string? LastError => Volatile.Read(ref lastError);
    public IReadOnlyList<NativeHidDeviceInfo> AvailableDevices => Volatile.Read(ref availableDevices);
    public CalibrationMode CurrentCalibrationMode
    {
        get { lock (calibrationLock) return calibrationMode; }
    }
    public CalibrationResult LastCalibrationResult
    {
        get { lock (calibrationLock) return calibrationResult; }
    }
    public int LeftRangeDirectionsCaptured
    {
        get
        {
            lock (calibrationLock)
            {
                if (calibrationMode != CalibrationMode.Range) return 0;
                return CountCapturedDirections(leftRangeRadii);
            }
        }
    }
    public int RightRangeDirectionsCaptured
    {
        get
        {
            lock (calibrationLock)
            {
                if (calibrationMode != CalibrationMode.Range) return 0;
                return CountCapturedDirections(rightRangeRadii);
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

    public void RefreshDevices()
    {
        Volatile.Write(ref availableDevices, NativeHid.ListNintendoProDevices());
    }

    public void SelectDevice(string? path)
    {
        if (string.Equals(settings.SelectedDevicePath, path, StringComparison.OrdinalIgnoreCase)) return;
        settings.SelectedDevicePath = string.IsNullOrWhiteSpace(path) ? null : path;
        saveSettings();
        lock (streamLock) currentStream?.Close();
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
            Array.Clear(leftRangeRadii);
            Array.Clear(rightRangeRadii);
        }
    }

    public bool FinishRangeCalibration()
    {
        lock (calibrationLock)
        {
            if (calibrationMode != CalibrationMode.Range) return false;
            if (CountCapturedDirections(leftRangeRadii) < 8 || CountCapturedDirections(rightRangeRadii) < 8)
            {
                calibrationResult = CalibrationResult.RangeIncomplete;
                return false;
            }

            settings.LeftStickCalibration.SetDirectionalRanges(leftRangeRadii);
            settings.RightStickCalibration.SetDirectionalRanges(rightRangeRadii);
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
                var requestedPath = settings.SelectedDevicePath;
                var device = NativeHid.TryOpenNintendoPro(requestedPath, out var discoveredDevices);
                Volatile.Write(ref availableDevices, discoveredDevices);
                if (device is null)
                {
                    SetDisconnected(null);
                    await Task.Delay(500, token);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(requestedPath) &&
                    string.Equals(settings.SelectedDevicePath, requestedPath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(requestedPath, device.Path, StringComparison.OrdinalIgnoreCase))
                {
                    settings.SelectedDevicePath = device.Path;
                    saveSettings();
                }

                await using (device.Stream)
                {
                    activeControllerKind = device.Kind;
                    lock (streamLock) currentStream = device.Stream;
                    Volatile.Write(ref lastError, null);
                    Interlocked.Exchange(ref packetNumber, 0);
                    Interlocked.Exchange(ref rumbleSequence, 0);
                    StopRumble();
                    if (device.Kind == ControllerKind.SwitchPro && device.IsUsb)
                        await TryInitializeUsbAsync(device.Stream, device.InputReportLength,
                            device.OutputReportLength, token);
                    if (device.Kind == ControllerKind.SwitchPro)
                    {
                        await TryEnableVibrationAsync(device.Stream, device.OutputReportLength, token);
                        await Task.Delay(10, token);
                        await TryEnableFullInputReportsAsync(device.Stream, device.OutputReportLength, token);
                    }
                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var rumbleTask = device.Kind == ControllerKind.Switch2Pro
                        ? RunSwitch2RumbleLoopAsync(device.Stream, device.OutputReportLength,
                            connectionCancellation.Token)
                        : RunRumbleLoopAsync(device.Stream, device.OutputReportLength,
                            connectionCancellation.Token);
                    var report = new byte[device.InputReportLength];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                                readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                                var count = await device.Stream.ReadAsync(report, readTimeout.Token);
                                if (count == 0) break;
                                var decodedSuccessfully = device.Kind == ControllerKind.Switch2Pro
                                    ? TryDecodeSwitch2(report.AsSpan(0, count), out var decoded)
                                    : TryDecode(report.AsSpan(0, count), out decoded);
                                if (count > 0 && decodedSuccessfully)
                                    Volatile.Write(ref snapshot, decoded);
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
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
            catch (OperationCanceledException)
            {
                SetDisconnected(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ObjectDisposedException or Win32Exception)
            {
                SetDisconnected(ex.GetType().Name);
            }
            finally
            {
                lock (streamLock) currentStream = null;
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
        activeControllerKind = ControllerKind.None;
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
                var leftStick = NormalizeStick(raw.LeftX, raw.LeftY, settings.LeftStickCalibration);
                var rightStick = NormalizeStick(raw.RightX, raw.RightY, settings.RightStickCalibration);
                state = CreateState(leftStick.X, leftStick.Y, rightStick.X, rightStick.Y,
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

    private bool TryDecodeSwitch2(ReadOnlySpan<byte> report, out ControllerSnapshot state)
    {
        state = ControllerSnapshot.Disconnected;
        if (report.Length < 17 || report[0] != 0x05) return false;

        var right = report[5];
        var shared = report[6];
        var left = report[7];
        var extra = report[8];
        var raw = new RawStickState(
            report[11] | ((report[12] & 0x0F) << 8),
            (report[12] >> 4) | (report[13] << 4),
            report[14] | ((report[15] & 0x0F) << 8),
            (report[15] >> 4) | (report[16] << 4));
        UpdateCalibration(raw);
        var leftStick = NormalizeStick(raw.LeftX, raw.LeftY, settings.LeftStickCalibration);
        var rightStick = NormalizeStick(raw.RightX, raw.RightY, settings.RightStickCalibration);
        var baseState = CreateState(leftStick.X, leftStick.Y, rightStick.X, rightStick.Y,
            right, shared, left);
        var buttons = baseState.Buttons;
        Add(ref buttons, (shared & 0x40) != 0, ResolveExtraButton(settings.CButtonMapping));
        Add(ref buttons, (extra & 0x02) != 0, ResolveExtraButton(settings.GlButtonMapping));
        Add(ref buttons, (extra & 0x01) != 0, ResolveExtraButton(settings.GrButtonMapping));
        state = baseState with { Buttons = buttons };
        return true;
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
        var leftStick = ApplyDeadzone(lx, ly, settings.LeftDeadzone);
        var rightStick = ApplyDeadzone(rx, ry, settings.RightDeadzone);
        return new ControllerSnapshot(true, leftStick.X, leftStick.Y, rightStick.X, rightStick.Y, buttons);
    }

    private static void Add(ref GamepadButtonsFlags buttons, bool pressed, GamepadButtonsFlags button)
    {
        if (pressed) buttons |= button;
    }

    private GamepadButtonsFlags ResolveExtraButton(ExtraButtonMapping mapping) => mapping switch
    {
        ExtraButtonMapping.A => settings.SwapAb ? GamepadButtonsFlags.Circle : GamepadButtonsFlags.Cross,
        ExtraButtonMapping.B => settings.SwapAb ? GamepadButtonsFlags.Cross : GamepadButtonsFlags.Circle,
        ExtraButtonMapping.X => settings.SwapXy ? GamepadButtonsFlags.Triangle : GamepadButtonsFlags.Square,
        ExtraButtonMapping.Y => settings.SwapXy ? GamepadButtonsFlags.Square : GamepadButtonsFlags.Triangle,
        ExtraButtonMapping.L => GamepadButtonsFlags.L1,
        ExtraButtonMapping.R => GamepadButtonsFlags.R1,
        ExtraButtonMapping.ZL => GamepadButtonsFlags.L2,
        ExtraButtonMapping.ZR => GamepadButtonsFlags.R2,
        ExtraButtonMapping.Plus => GamepadButtonsFlags.Start,
        ExtraButtonMapping.Minus => GamepadButtonsFlags.Select,
        ExtraButtonMapping.LeftStick => GamepadButtonsFlags.L3,
        ExtraButtonMapping.RightStick => GamepadButtonsFlags.R3,
        ExtraButtonMapping.DPadUp => GamepadButtonsFlags.DPadUp,
        ExtraButtonMapping.DPadDown => GamepadButtonsFlags.DPadDown,
        ExtraButtonMapping.DPadLeft => GamepadButtonsFlags.DPadLeft,
        ExtraButtonMapping.DPadRight => GamepadButtonsFlags.DPadRight,
        _ => GamepadButtonsFlags.None,
    };

    private static (float X, float Y) NormalizeStick(int x, int y, StickCalibration calibration)
    {
        var dx = x - calibration.CenterX;
        var dy = y - calibration.CenterY;
        if (dx == 0 && dy == 0) return (0, 0);

        var angle = MathF.Atan2(dy, dx);
        if (angle < 0) angle += MathF.Tau;
        var sector = angle / (MathF.PI / 4f);
        var lower = (int)MathF.Floor(sector) & 7;
        var upper = (lower + 1) & 7;
        var fraction = sector - MathF.Floor(sector);
        var boundary = calibration.DirectionalRanges[lower] +
            (calibration.DirectionalRanges[upper] - calibration.DirectionalRanges[lower]) * fraction;
        var scale = 1f / Math.Max(boundary, 1f);
        var normalizedX = dx * scale;
        var normalizedY = dy * scale;
        var magnitude = MathF.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);
        if (magnitude > 1f)
        {
            normalizedX /= magnitude;
            normalizedY /= magnitude;
        }
        return (normalizedX, normalizedY);
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
                CaptureDirection(raw.LeftX - settings.LeftStickCalibration.CenterX,
                    raw.LeftY - settings.LeftStickCalibration.CenterY, leftRangeRadii);
                CaptureDirection(raw.RightX - settings.RightStickCalibration.CenterX,
                    raw.RightY - settings.RightStickCalibration.CenterY, rightRangeRadii);
            }
        }
        if (save) saveSettings();
    }

    private static void CaptureDirection(int x, int y, int[] radii)
    {
        const int requiredTravel = 512;
        var radius = (int)MathF.Round(MathF.Sqrt(x * x + y * y));
        if (radius < requiredTravel) return;
        var angle = MathF.Atan2(y, x);
        if (angle < 0) angle += MathF.Tau;
        var direction = ((int)MathF.Round(angle / (MathF.PI / 4f))) & 7;
        radii[direction] = Math.Max(radii[direction], radius);
    }

    private static int CountCapturedDirections(int[] radii) => radii.Count(radius => radius > 0);

    private static (float X, float Y) ApplyDeadzone(float x, float y, float deadzone)
    {
        var magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= deadzone || magnitude == 0) return (0, 0);
        var outputMagnitude = Math.Clamp((magnitude - deadzone) / (1 - deadzone), 0f, 1f);
        var scale = outputMagnitude / magnitude;
        return (x * scale, y * scale);
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

    private async Task RunSwitch2RumbleLoopAsync(FileStream stream, int outputReportLength,
        CancellationToken token)
    {
        var lastLeft = -1;
        var lastRight = -1;
        var lastWrite = 0L;
        var refreshTicks = Stopwatch.Frequency * 12 / 1000;
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
                if (stopImmediately || (changed && (lastWrite == 0 || elapsed >= refreshTicks)) ||
                    (active && elapsed >= refreshTicks))
                {
                    await WriteSwitch2RumbleAsync(stream, outputReportLength, left, right, token);
                    lastLeft = left;
                    lastRight = right;
                    lastWrite = Stopwatch.GetTimestamp();
                }
                await Task.Delay(4, token);
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
                await WriteSwitch2RumbleAsync(stream, outputReportLength, 0, 0, stopTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task WriteSwitch2RumbleAsync(FileStream stream, int outputReportLength,
        int leftMotor, int rightMotor, CancellationToken token)
    {
        if (outputReportLength < 64)
            throw new IOException("Switch 2 HID output report is shorter than 64 bytes.");
        var report = new byte[outputReportLength];
        var low = (ushort)Math.Clamp((long)leftMotor * Switch2MaximumRumbleAmplitude / ushort.MaxValue,
            0, Switch2MaximumRumbleAmplitude);
        var high = (ushort)Math.Clamp((long)rightMotor * Switch2MaximumRumbleAmplitude / ushort.MaxValue,
            0, Switch2MaximumRumbleAmplitude);
        Switch2Rumble.Encode(report,
            (byte)((Interlocked.Increment(ref rumbleSequence) - 1) & 0x0F), high, low);
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

    private sealed record RawStickState(int LeftX, int LeftY, int RightX, int RightY);
}

internal enum CalibrationMode { None, Center, Range }
internal enum CalibrationResult { None, CenterComplete, RangeComplete, RangeIncomplete, Reset, Disconnected }
