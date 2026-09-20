using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NintendoProBridge;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/npro";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("NintendoProBridge");
    private readonly SettingsWindow settingsWindow;
    private readonly ProControllerInput controllerInput;

    public string Name => "Nintendo Pro Bridge";

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IGameInteropProvider interop)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        var configDirectory = pluginInterface.ConfigDirectory?.FullName
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "pluginConfigs", "NintendoProBridge");
        var settingsPath = Path.Combine(configDirectory, "settings.json");
        var firstRun = !File.Exists(settingsPath);
        var settings = PluginSettings.Load(settingsPath);
        var saveLock = new object();
        Action saveSettings = () =>
        {
            lock (saveLock) Save(settingsPath, settings);
        };

        controllerInput = new ProControllerInput(interop, settings, saveSettings);
        settingsWindow = new SettingsWindow(settings, controllerInput,
            saveSettings, () => pluginInterface.UiLanguage);
        windowSystem.AddWindow(settingsWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Nintendo Pro Bridge 设置。",
        });

        if (firstRun) settingsWindow.IsOpen = true;
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(Command);
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windowSystem.RemoveAllWindows();
        controllerInput.Dispose();
    }

    private void Draw() => windowSystem.Draw();
    private void OpenSettings() => settingsWindow.IsOpen = true;
    private void OnCommand(string command, string arguments) => settingsWindow.IsOpen = !settingsWindow.IsOpen;

    private static void Save(string path, PluginSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, PluginSettings.JsonOptions));
    }
}

internal sealed class SettingsWindow : Window
{
    private const string WindowId = "###NintendoProBridgeSettings";
    private const string DonationAddress = "TF3TK5jT6dBVqY3JTpGJaVmzhBzryq8DhN";

    private static readonly (string Code, string Name)[] LanguageOptions =
    [
        ("auto", ""),
        ("zh-cn", "简体中文"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("zh-tw", "繁體中文"),
        ("ko", "한국어"),
        ("ja", "日本語"),
    ];

    private static readonly (ExtraButtonMapping Value, string TextKey)[] ExtraButtonOptions =
    [
        (ExtraButtonMapping.None, "mappingNone"),
        (ExtraButtonMapping.A, "mappingA"), (ExtraButtonMapping.B, "mappingB"),
        (ExtraButtonMapping.X, "mappingX"), (ExtraButtonMapping.Y, "mappingY"),
        (ExtraButtonMapping.L, "mappingL"), (ExtraButtonMapping.R, "mappingR"),
        (ExtraButtonMapping.ZL, "mappingZL"), (ExtraButtonMapping.ZR, "mappingZR"),
        (ExtraButtonMapping.LeftStick, "mappingLeftStick"),
        (ExtraButtonMapping.RightStick, "mappingRightStick"),
        (ExtraButtonMapping.Plus, "mappingPlus"), (ExtraButtonMapping.Minus, "mappingMinus"),
        (ExtraButtonMapping.DPadUp, "mappingDPadUp"),
        (ExtraButtonMapping.DPadDown, "mappingDPadDown"),
        (ExtraButtonMapping.DPadLeft, "mappingDPadLeft"),
        (ExtraButtonMapping.DPadRight, "mappingDPadRight"),
    ];

    private readonly PluginSettings settings;
    private readonly ProControllerInput controller;
    private readonly Action save;
    private readonly Func<string> language;
    private bool donationCopied;

    public SettingsWindow(PluginSettings settings, ProControllerInput controller, Action save, Func<string> language)
        : base(LocalizedText.Get(
            string.IsNullOrWhiteSpace(settings.Language) || settings.Language == "auto"
                ? language()
                : settings.Language,
            "windowTitle") + WindowId)
    {
        this.settings = settings;
        this.controller = controller;
        this.save = save;
        this.language = language;
        Size = new Vector2(300, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        WindowName = T("windowTitle") + WindowId;
        ImGui.TextWrapped(T("intro"));
        ImGui.Spacing();
        ImGui.TextColored(controller.IsConnected
                ? new Vector4(.3f, 1f, .45f, 1f)
                : new Vector4(1f, .7f, .2f, 1f),
            T(controller.IsConnected ? "connected" : "disconnected"));
        ImGui.TextWrapped(T(controller.IsConnected ? "activeDetail" : "connectDetail"));
        if (!string.IsNullOrWhiteSpace(controller.LastError))
            ImGui.TextColored(new Vector4(1f, .55f, .3f, 1f), T("readError"));

        var devices = controller.AvailableDevices;
        var selectedDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Path, settings.SelectedDevicePath, StringComparison.OrdinalIgnoreCase));
        var devicePreview = string.IsNullOrWhiteSpace(settings.SelectedDevicePath)
            ? T("automaticDevice")
            : selectedDevice?.DisplayName ?? T("selectedDeviceMissing");
        ImGui.Text(T("controllerDevice"));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##controllerDevice", devicePreview))
        {
            var automatic = string.IsNullOrWhiteSpace(settings.SelectedDevicePath);
            if (ImGui.Selectable(T("automaticDevice"), automatic)) controller.SelectDevice(null);
            if (automatic) ImGui.SetItemDefaultFocus();
            for (var index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                var selected = string.Equals(device.Path, settings.SelectedDevicePath,
                    StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{device.DisplayName}##device{index}", selected))
                    controller.SelectDevice(device.Path);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.Button(T("refreshDevices"))) controller.RefreshDevices();

        ImGui.Separator();
        var changed = false;
        var enabled = settings.Enabled;
        var focusOnly = settings.FocusOnly;
        var enableRumble = settings.EnableRumble;
        var swapAb = settings.SwapAb;
        var swapXy = settings.SwapXy;
        var leftDeadzone = settings.LeftDeadzone;
        var rightDeadzone = settings.RightDeadzone;
        var currentLanguage = LanguageOptions.FirstOrDefault(x => x.Code == settings.Language);
        var languageName = string.IsNullOrEmpty(currentLanguage.Code) || currentLanguage.Code == "auto"
            ? T("autoLanguage")
            : currentLanguage.Name;
        if (ImGui.BeginCombo(T("language"), languageName))
        {
            foreach (var option in LanguageOptions)
            {
                var selected = settings.Language == option.Code;
                var optionName = option.Code == "auto" ? T("autoLanguage") : option.Name;
                if (ImGui.Selectable(optionName, selected))
                {
                    settings.Language = option.Code;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        changed |= ImGui.Checkbox(T("enabled"), ref enabled);
        ImGui.SameLine();
        changed |= ImGui.Checkbox(T("focusOnly"), ref focusOnly);
        changed |= ImGui.Checkbox(T("enableRumble"), ref enableRumble);
        ImGui.SameLine();
        if (!controller.IsConnected || !enabled || !enableRumble) ImGui.BeginDisabled();
        if (ImGui.Button(T("testRumble"))) controller.TestRumble();
        if (!controller.IsConnected || !enabled || !enableRumble) ImGui.EndDisabled();
        changed |= ImGui.Checkbox(T("swapAb"), ref swapAb);
        changed |= ImGui.Checkbox(T("swapXy"), ref swapXy);
        changed |= ImGui.SliderFloat(T("leftDeadzone"), ref leftDeadzone, 0f, .5f, "%.2f");
        changed |= ImGui.SliderFloat(T("rightDeadzone"), ref rightDeadzone, 0f, .5f, "%.2f");
        settings.Enabled = enabled;
        settings.FocusOnly = focusOnly;
        settings.EnableRumble = enableRumble;
        settings.SwapAb = swapAb;
        settings.SwapXy = swapXy;
        settings.LeftDeadzone = leftDeadzone;
        settings.RightDeadzone = rightDeadzone;

        var switch2Selected = selectedDevice?.Kind == ControllerKind.Switch2Pro ||
            (string.IsNullOrWhiteSpace(settings.SelectedDevicePath) &&
             controller.ActiveControllerKind == ControllerKind.Switch2Pro);
        if (switch2Selected)
        {
            ImGui.Separator();
            ImGui.Text(T("switch2Mapping"));
            ImGui.TextWrapped(T("switch2MappingGuide"));
            var cMapping = settings.CButtonMapping;
            var glMapping = settings.GlButtonMapping;
            var grMapping = settings.GrButtonMapping;
            changed |= DrawMappingCombo("C", ref cMapping);
            changed |= DrawMappingCombo("GL", ref glMapping);
            changed |= DrawMappingCombo("GR", ref grMapping);
            settings.CButtonMapping = cMapping;
            settings.GlButtonMapping = glMapping;
            settings.GrButtonMapping = grMapping;
        }
        if (changed) save();

        ImGui.Separator();
        ImGui.Text(T("stickCalibration"));
        ImGui.TextWrapped(T("calibrationGuide"));
        var calibrationMode = controller.CurrentCalibrationMode;
        if (calibrationMode == CalibrationMode.None)
        {
            if (!controller.IsConnected) ImGui.BeginDisabled();
            if (ImGui.Button(T("calibrateCenter"))) controller.StartCenterCalibration();
            ImGui.SameLine();
            if (ImGui.Button(T("calibrateRange"))) controller.StartRangeCalibration();
            if (!controller.IsConnected) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(T("resetCalibration"))) controller.ResetCalibration();
        }
        else if (calibrationMode == CalibrationMode.Center)
        {
            ImGui.TextColored(new Vector4(1f, .75f, .2f, 1f), T("centerInProgress"));
            if (ImGui.Button(T("cancelCalibration"))) controller.CancelCalibration();
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, .75f, .2f, 1f),
                string.Format(T("rangeInProgress"), controller.LeftRangeDirectionsCaptured,
                    controller.RightRangeDirectionsCaptured));
            if (ImGui.Button(T("finishCalibration"))) controller.FinishRangeCalibration();
            ImGui.SameLine();
            if (ImGui.Button(T("cancelCalibration"))) controller.CancelCalibration();
        }

        var calibrationResult = controller.LastCalibrationResult;
        if (calibrationResult != CalibrationResult.None)
        {
            var successful = calibrationResult != CalibrationResult.RangeIncomplete;
            var resultKey = calibrationResult switch
            {
                CalibrationResult.CenterComplete => "centerComplete",
                CalibrationResult.RangeComplete => "rangeComplete",
                CalibrationResult.RangeIncomplete => "rangeIncomplete",
                CalibrationResult.Reset => "calibrationReset",
                CalibrationResult.Disconnected => "calibrationDisconnected",
                _ => "calibrationReset",
            };
            ImGui.TextColored(successful
                ? new Vector4(.3f, 1f, .45f, 1f)
                : new Vector4(1f, .55f, .3f, 1f), T(resultKey));
        }

        ImGui.Spacing();
        ImGui.TextDisabled(T("command"));

        ImGui.Separator();
        ImGui.Text(T("donation"));
        ImGui.TextWrapped(T("donationGuide"));
        ImGui.Text($"{T("donationNetwork")}: TRON (TRC-20) · {T("donationCurrency")}: USDT");
        var donationAddress = DonationAddress;
        ImGui.InputText("##donationAddress", ref donationAddress, DonationAddress.Length + 1,
            ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button(T("copyDonationAddress")))
        {
            ImGui.SetClipboardText(DonationAddress);
            donationCopied = true;
        }
        if (donationCopied) ImGui.TextColored(new Vector4(.3f, 1f, .45f, 1f), T("donationCopied"));
    }

    private string T(string key) => LocalizedText.Get(
        string.IsNullOrWhiteSpace(settings.Language) || settings.Language == "auto"
            ? language()
            : settings.Language,
        key);

    private bool DrawMappingCombo(string sourceButton, ref ExtraButtonMapping mapping)
    {
        var changed = false;
        var currentMapping = mapping;
        var current = ExtraButtonOptions.FirstOrDefault(option => option.Value == currentMapping);
        var preview = T(string.IsNullOrEmpty(current.TextKey) ? "mappingNone" : current.TextKey);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo($"{sourceButton}##mapping{sourceButton}", preview))
        {
            foreach (var option in ExtraButtonOptions)
            {
                var selected = mapping == option.Value;
                if (ImGui.Selectable(T(option.TextKey), selected))
                {
                    mapping = option.Value;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}

internal static class LocalizedText
{
    private static readonly Dictionary<string, string[]> Values = new()
    {
        ["windowTitle"] = ["Nintendo Pro Bridge 设置", "Nintendo Pro Bridge Settings", "Nintendo Pro Bridge – Einstellungen", "Paramètres de Nintendo Pro Bridge", "Nintendo Pro Bridge 設定", "Nintendo Pro Bridge 설정", "Nintendo Pro Bridge 設定"],
        ["intro"] = ["让Nintendo Switch Pro 手柄能被 FFXIV 识别并正常游玩。", "Use a Nintendo Switch Pro Controller as an FFXIV gamepad.", "Nintendo Switch Pro Controller als FFXIV-Gamepad verwenden.", "Utilisez une manette Nintendo Switch Pro comme manette FFXIV.", "讓Nintendo Switch Pro 控制器能被 FFXIV 識別並正常遊玩。", "Nintendo Switch Pro 컨트롤러를 FFXIV 게임패드로 사용합니다.", "Nintendo Switch ProコントローラーをFFXIVのゲームパッドとして使用します。"],
        ["connected"] = ["已连接", "Connected", "Verbunden", "Connectée", "已連接", "연결됨", "接続済み"],
        ["disconnected"] = ["未检测到可读取的 Pro 手柄", "No readable Pro Controller detected", "Kein lesbarer Pro Controller erkannt", "Aucune manette Pro lisible détectée", "未偵測到可讀取的 Pro 控制器", "읽을 수 있는 Pro 컨트롤러를 찾지 못했습니다", "読み取り可能なProコントローラーが見つかりません"],
        ["activeDetail"] = ["FFXIV 手柄输入已启用。", "FFXIV gamepad input is active.", "FFXIV-Gamepad-Eingabe ist aktiv.", "L’entrée manette de FFXIV est active.", "FFXIV 控制器輸入已啟用。", "FFXIV 게임패드 입력이 활성화되었습니다.", "FFXIVのゲームパッド入力が有効です。"],
        ["connectDetail"] = ["连接手柄并按任意键。若 Steam 独占了设备，请关闭该游戏的 Steam Input 后重连。", "Connect the controller and press any button. If Steam has exclusive access, disable Steam Input for this game and reconnect.", "Controller verbinden und eine Taste drücken. Falls Steam exklusiv zugreift, Steam Input für dieses Spiel deaktivieren und neu verbinden.", "Connectez la manette et appuyez sur un bouton. Si Steam y accède exclusivement, désactivez Steam Input pour ce jeu puis reconnectez-la.", "連接控制器並按任意鍵。若 Steam 獨佔裝置，請關閉此遊戲的 Steam Input 後重新連線。", "컨트롤러를 연결하고 아무 버튼이나 누르세요. Steam이 장치를 독점하면 이 게임의 Steam Input을 끄고 다시 연결하세요.", "コントローラーを接続してボタンを押してください。Steamが占有している場合は、このゲームのSteam Inputを無効にして再接続してください。"],
        ["readError"] = ["手柄存在但暂时无法读取，可能正被 Steam 或其他手柄工具独占。", "The controller exists but cannot currently be read; Steam or another controller tool may have exclusive access.", "Der Controller ist vorhanden, kann aber nicht gelesen werden; Steam oder ein anderes Tool könnte exklusiv zugreifen.", "La manette est présente mais illisible ; Steam ou un autre outil peut disposer d'un accès exclusif.", "控制器存在但暫時無法讀取，可能正被 Steam 或其他工具獨佔。", "컨트롤러가 있지만 읽을 수 없습니다. Steam 또는 다른 도구가 독점 중일 수 있습니다.", "コントローラーは存在しますが読み取れません。Steamなどが占有している可能性があります。"],
        ["controllerDevice"] = ["手柄设备", "Controller device", "Controller-Gerät", "Périphérique de manette", "控制器裝置", "컨트롤러 장치", "コントローラー機器"],
        ["automaticDevice"] = ["自动选择", "Select automatically", "Automatisch auswählen", "Sélection automatique", "自動選擇", "자동 선택", "自動選択"],
        ["selectedDeviceMissing"] = ["已选设备未连接", "Selected device is not connected", "Ausgewähltes Gerät ist nicht verbunden", "Le périphérique sélectionné n’est pas connecté", "已選裝置未連接", "선택한 장치가 연결되지 않았습니다", "選択した機器が接続されていません"],
        ["refreshDevices"] = ["刷新设备", "Refresh devices", "Geräte aktualisieren", "Actualiser les périphériques", "重新整理裝置", "장치 새로고침", "機器を更新"],
        ["language"] = ["界面语言", "Interface language", "Oberflächensprache", "Langue de l’interface", "介面語言", "인터페이스 언어", "表示言語"],
        ["autoLanguage"] = ["跟随游戏", "Follow game language", "Spielsprache verwenden", "Suivre la langue du jeu", "跟隨遊戲", "게임 언어 따르기", "ゲーム言語に合わせる"],
        ["enabled"] = ["启用手柄适配", "Enable controller support", "Controller-Unterstützung aktivieren", "Activer la prise en charge de la manette", "啟用控制器適配", "컨트롤러 지원 활성화", "コントローラー対応を有効にする"],
        ["focusOnly"] = ["仅在游戏窗口焦点时有效", "Only when the game window is focused", "Nur bei fokussiertem Spielfenster", "Uniquement lorsque la fenêtre du jeu est active", "僅在遊戲視窗取得焦點時有效", "게임 창에 포커스가 있을 때만 적용", "ゲームウィンドウがフォーカスされているときのみ有効"],
        ["enableRumble"] = ["启用震动", "Enable rumble", "Vibration aktivieren", "Activer les vibrations", "啟用震動", "진동 활성화", "振動を有効にする"],
        ["testRumble"] = ["测试", "Test", "Testen", "Tester", "測試", "테스트", "テスト"],
        ["swapAb"] = ["交换 A / B", "Swap A / B", "A / B tauschen", "Inverser A / B", "交換 A / B", "A / B 교체", "A / B を入れ替える"],
        ["swapXy"] = ["交换 X / Y", "Swap X / Y", "X / Y tauschen", "Inverser X / Y", "交換 X / Y", "X / Y 교체", "X / Y を入れ替える"],
        ["leftDeadzone"] = ["左摇杆死区", "Left stick deadzone", "Totzone linker Stick", "Zone morte du stick gauche", "左搖桿死區", "왼쪽 스틱 데드존", "左スティックのデッドゾーン"],
        ["rightDeadzone"] = ["右摇杆死区", "Right stick deadzone", "Totzone rechter Stick", "Zone morte du stick droit", "右搖桿死區", "오른쪽 스틱 데드존", "右スティックのデッドゾーン"],
        ["switch2Mapping"] = ["Switch 2 Pro 附加键映射", "Switch 2 Pro extra button mapping", "Switch 2 Pro-Zusatztasten", "Touches supplémentaires Switch 2 Pro", "Switch 2 Pro 附加鍵映射", "Switch 2 Pro 추가 버튼 매핑", "Switch 2 Pro追加ボタン割り当て"],
        ["switch2MappingGuide"] = ["为 C、GL 和 GR 选择最终幻想14中的手柄按键。", "Choose the FFXIV gamepad button produced by C, GL, and GR.", "FFXIV-Gamepad-Tasten für C, GL und GR auswählen.", "Choisissez les touches de manette FFXIV associées à C, GL et GR.", "為 C、GL 和 GR 選擇最終幻想14中的控制器按鍵。", "C, GL, GR에 연결할 FFXIV 게임패드 버튼을 선택하세요.", "C、GL、GRに割り当てるFFXIVゲームパッドボタンを選択します。"],
        ["mappingNone"] = ["不映射", "Not mapped", "Nicht zugewiesen", "Non attribué", "不映射", "매핑 안 함", "割り当てなし"],
        ["mappingA"] = ["A 键", "A button", "A-Taste", "Touche A", "A 鍵", "A 버튼", "Aボタン"],
        ["mappingB"] = ["B 键", "B button", "B-Taste", "Touche B", "B 鍵", "B 버튼", "Bボタン"],
        ["mappingX"] = ["X 键", "X button", "X-Taste", "Touche X", "X 鍵", "X 버튼", "Xボタン"],
        ["mappingY"] = ["Y 键", "Y button", "Y-Taste", "Touche Y", "Y 鍵", "Y 버튼", "Yボタン"],
        ["mappingL"] = ["L 键", "L button", "L-Taste", "Touche L", "L 鍵", "L 버튼", "Lボタン"],
        ["mappingR"] = ["R 键", "R button", "R-Taste", "Touche R", "R 鍵", "R 버튼", "Rボタン"],
        ["mappingZL"] = ["ZL 键", "ZL button", "ZL-Taste", "Touche ZL", "ZL 鍵", "ZL 버튼", "ZLボタン"],
        ["mappingZR"] = ["ZR 键", "ZR button", "ZR-Taste", "Touche ZR", "ZR 鍵", "ZR 버튼", "ZRボタン"],
        ["mappingLeftStick"] = ["L 摇杆键", "Left stick button", "Linker Stick-Klick", "Clic stick gauche", "L 搖桿鍵", "왼쪽 스틱 버튼", "Lスティックボタン"],
        ["mappingRightStick"] = ["R 摇杆键", "Right stick button", "Rechter Stick-Klick", "Clic stick droit", "R 搖桿鍵", "오른쪽 스틱 버튼", "Rスティックボタン"],
        ["mappingPlus"] = ["＋键", "+ button", "+-Taste", "Touche +", "＋鍵", "+ 버튼", "＋ボタン"],
        ["mappingMinus"] = ["－键", "- button", "−-Taste", "Touche -", "－鍵", "- 버튼", "－ボタン"],
        ["mappingDPadUp"] = ["十字键上", "D-pad up", "Steuerkreuz oben", "Croix haut", "十字鍵上", "십자키 위", "十字キー上"],
        ["mappingDPadDown"] = ["十字键下", "D-pad down", "Steuerkreuz unten", "Croix bas", "十字鍵下", "십자키 아래", "十字キー下"],
        ["mappingDPadLeft"] = ["十字键左", "D-pad left", "Steuerkreuz links", "Croix gauche", "十字鍵左", "십자키 왼쪽", "十字キー左"],
        ["mappingDPadRight"] = ["十字键右", "D-pad right", "Steuerkreuz rechts", "Croix droite", "十字鍵右", "십자키 오른쪽", "十字キー右"],
        ["stickCalibration"] = ["摇杆校准", "Stick calibration", "Stick-Kalibrierung", "Calibrage des sticks", "搖桿校準", "스틱 보정", "スティック調整"],
        ["calibrationGuide"] = ["先让双摇杆回中进行回中校准，再开始满推校准并将两个摇杆沿外圈完整转动。", "Calibrate the centered sticks first. Then start full-range calibration and rotate both sticks around their full outer edge.", "Zuerst beide Sticks in Mittelstellung kalibrieren. Danach die Bereichskalibrierung starten und beide Sticks vollständig am Rand entlang drehen.", "Calibrez d’abord les sticks au repos, puis lancez le calibrage complet et faites tourner les deux sticks sur tout leur contour.", "先讓雙搖桿回中進行回中校準，再開始滿推校準並將兩個搖桿沿外圈完整轉動。", "먼저 두 스틱의 중앙을 보정한 뒤 전체 범위 보정을 시작하고 두 스틱을 바깥쪽 가장자리를 따라 완전히 돌리세요.", "まず両スティックを中央に戻して中央調整を行い、その後フルレンジ調整を開始して両方を外周いっぱいに回してください。"],
        ["calibrateCenter"] = ["回中校准", "Calibrate center", "Mitte kalibrieren", "Calibrer le centre", "回中校準", "중앙 보정", "中央を調整"],
        ["calibrateRange"] = ["满推校准", "Full-range calibration", "Bereich kalibrieren", "Calibrer la course", "滿推校準", "전체 범위 보정", "フルレンジ調整"],
        ["resetCalibration"] = ["重置", "Reset", "Zurücksetzen", "Réinitialiser", "重設", "초기화", "リセット"],
        ["centerInProgress"] = ["请松开双摇杆，正在采样（1 秒）…", "Release both sticks. Sampling for 1 second…", "Beide Sticks loslassen. Messung läuft 1 Sekunde…", "Relâchez les deux sticks. Mesure pendant 1 seconde…", "請鬆開雙搖桿，正在取樣（1 秒）…", "두 스틱에서 손을 떼세요. 1초 동안 측정합니다…", "両スティックから手を離してください。1秒間測定します…"],
        ["rangeInProgress"] = ["沿外圈转动摇杆：左 {0}/8，右 {1}/8。", "Rotate the sticks around the outer edge: left {0}/8, right {1}/8.", "Sticks am Rand entlang drehen: links {0}/8, rechts {1}/8.", "Faites tourner les sticks sur le contour : gauche {0}/8, droite {1}/8.", "沿外圈轉動搖桿：左 {0}/8，右 {1}/8。", "스틱을 바깥쪽 가장자리를 따라 돌리세요: 왼쪽 {0}/8, 오른쪽 {1}/8.", "スティックを外周に沿って回してください：左 {0}/8、右 {1}/8。"],
        ["finishCalibration"] = ["完成", "Finish", "Abschließen", "Terminer", "完成", "완료", "完了"],
        ["cancelCalibration"] = ["取消", "Cancel", "Abbrechen", "Annuler", "取消", "취소", "キャンセル"],
        ["centerComplete"] = ["回中校准完成。", "Center calibration complete.", "Mittelstellung kalibriert.", "Calibrage du centre terminé.", "回中校準完成。", "중앙 보정이 완료되었습니다.", "中央調整が完了しました。"],
        ["rangeComplete"] = ["满推校准完成。", "Full-range calibration complete.", "Bereichskalibrierung abgeschlossen.", "Calibrage de la course terminé.", "滿推校準完成。", "전체 범위 보정이 완료되었습니다.", "フルレンジ調整が完了しました。"],
        ["rangeIncomplete"] = ["每根摇杆都需要记录 8 个端点，请继续沿外圈转动。", "Each stick needs all 8 endpoints. Keep rotating them around the outer edge.", "Für jeden Stick werden alle 8 Endpunkte benötigt. Weiter am Rand entlang drehen.", "Chaque stick doit enregistrer ses 8 extrémités. Continuez à les faire tourner sur le contour.", "每根搖桿都需要記錄 8 個端點，請繼續沿外圈轉動。", "각 스틱마다 8개 끝점이 모두 필요합니다. 바깥쪽 가장자리를 따라 계속 돌리세요.", "各スティックで8つすべての端点が必要です。外周に沿って回し続けてください。"],
        ["calibrationReset"] = ["摇杆校准已重置。", "Stick calibration reset.", "Stick-Kalibrierung zurückgesetzt.", "Calibrage des sticks réinitialisé.", "搖桿校準已重設。", "스틱 보정이 초기화되었습니다.", "スティック調整をリセットしました。"],
        ["calibrationDisconnected"] = ["手柄已断开，校准已取消。", "Controller disconnected; calibration cancelled.", "Controller getrennt; Kalibrierung abgebrochen.", "Manette déconnectée ; calibrage annulé.", "控制器已中斷連線，校準已取消。", "컨트롤러 연결이 끊겨 보정이 취소되었습니다.", "コントローラーが切断されたため、調整を中止しました。"],
        ["command"] = ["设置命令：/npro", "Settings command: /npro", "Einstellungsbefehl: /npro", "Commande des paramètres : /npro", "設定指令：/npro", "설정 명령어: /npro", "設定コマンド：/npro"],
        ["donation"] = ["支持开发", "Support development", "Entwicklung unterstützen", "Soutenir le développement", "支持開發", "개발 후원", "開発を支援"],
        ["donationGuide"] = ["如果这个插件对你有帮助，可以使用 USDT（TRON）支持开发。转账前请确认网络为 TRON（TRC-20）。", "If this plugin helps you, you can support development with USDT on TRON. Confirm the network is TRON (TRC-20) before sending.", "Wenn dieses Plugin hilfreich ist, kannst du die Entwicklung mit USDT über TRON unterstützen. Vor dem Senden das Netzwerk TRON (TRC-20) prüfen.", "Si ce plugin vous est utile, vous pouvez soutenir son développement avec de l’USDT sur TRON. Vérifiez le réseau TRON (TRC-20) avant l’envoi.", "如果這個插件對你有幫助，可以使用 USDT（TRON）支持開發。轉帳前請確認網路為 TRON（TRC-20）。", "이 플러그인이 도움이 되었다면 TRON의 USDT로 개발을 후원할 수 있습니다. 전송 전에 네트워크가 TRON(TRC-20)인지 확인하세요.", "このプラグインが役立った場合は、TRON上のUSDTで開発を支援できます。送金前にネットワークがTRON（TRC-20）であることを確認してください。"],
        ["donationNetwork"] = ["网络", "Network", "Netzwerk", "Réseau", "網路", "네트워크", "ネットワーク"],
        ["donationCurrency"] = ["币种", "Currency", "Währung", "Devise", "幣種", "통화", "通貨"],
        ["copyDonationAddress"] = ["复制地址", "Copy address", "Adresse kopieren", "Copier l’adresse", "複製地址", "주소 복사", "アドレスをコピー"],
        ["donationCopied"] = ["地址已复制。", "Address copied.", "Adresse kopiert.", "Adresse copiée.", "地址已複製。", "주소가 복사되었습니다.", "アドレスをコピーしました。"],
    };

    public static string Get(string language, string key)
    {
        var index = language.ToLowerInvariant() switch
        {
            "en" or "en-us" => 1, "de" or "de-de" => 2, "fr" or "fr-fr" => 3,
            "zh-tw" or "zh-hant" => 4, "ko" or "ko-kr" => 5, "ja" or "ja-jp" => 6, _ => 0,
        };
        return Values[key][index];
    }
}

public sealed class PluginSettings
{
    public string Language { get; set; } = "auto";
    public bool Enabled { get; set; } = true;
    public bool FocusOnly { get; set; }
    public bool EnableRumble { get; set; } = true;
    public bool SwapAb { get; set; }
    public bool SwapXy { get; set; }
    public float LeftDeadzone { get; set; } = .35f;
    public float RightDeadzone { get; set; } = .35f;
    public string? SelectedDevicePath { get; set; }
    public ExtraButtonMapping CButtonMapping { get; set; }
    public ExtraButtonMapping GlButtonMapping { get; set; }
    public ExtraButtonMapping GrButtonMapping { get; set; }
    public StickCalibration LeftStickCalibration { get; set; } = new();
    public StickCalibration RightStickCalibration { get; set; } = new();
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PluginSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path), JsonOptions) ?? new();
            settings.LeftStickCalibration ??= new();
            settings.RightStickCalibration ??= new();
            settings.LeftStickCalibration.Validate();
            settings.RightStickCalibration.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
}

public enum ExtraButtonMapping
{
    None, A, B, X, Y, L, R, ZL, ZR, LeftStick, RightStick, Plus, Minus,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}

public sealed class StickCalibration
{
    private const int MaximumRadius = 5792;
    public int CenterX { get; set; } = 2048;
    public int CenterY { get; set; } = 2048;
    public int MinimumX { get; set; } = 500;
    public int MaximumX { get; set; } = 3500;
    public int MinimumY { get; set; } = 500;
    public int MaximumY { get; set; } = 3500;
    public int[] DirectionalRanges { get; set; } = CreateDefaultDirectionalRanges();

    public void SetCenter(int x, int y)
    {
        CenterX = Math.Clamp(x, 1, 4094);
        CenterY = Math.Clamp(y, 1, 4094);
        Validate();
    }

    public void SetDirectionalRanges(IReadOnlyList<int> ranges)
    {
        if (ranges.Count != 8) throw new ArgumentException("Eight directional ranges are required.", nameof(ranges));
        DirectionalRanges = ranges.Select(radius => Math.Clamp(radius, 256, MaximumRadius)).ToArray();
        MaximumX = Math.Clamp(CenterX + DirectionalRanges[0], CenterX + 1, 4095);
        MaximumY = Math.Clamp(CenterY + DirectionalRanges[2], CenterY + 1, 4095);
        MinimumX = Math.Clamp(CenterX - DirectionalRanges[4], 0, CenterX - 1);
        MinimumY = Math.Clamp(CenterY - DirectionalRanges[6], 0, CenterY - 1);
        Validate();
    }

    public void Reset()
    {
        CenterX = CenterY = 2048;
        MinimumX = MinimumY = 500;
        MaximumX = MaximumY = 3500;
        DirectionalRanges = CreateDefaultDirectionalRanges();
    }

    public void Validate()
    {
        CenterX = Math.Clamp(CenterX, 1, 4094);
        CenterY = Math.Clamp(CenterY, 1, 4094);
        MinimumX = Math.Clamp(MinimumX, 0, CenterX - 1);
        MaximumX = Math.Clamp(MaximumX, CenterX + 1, 4095);
        MinimumY = Math.Clamp(MinimumY, 0, CenterY - 1);
        MaximumY = Math.Clamp(MaximumY, CenterY + 1, 4095);
        if (DirectionalRanges is null || DirectionalRanges.Length != 8)
            DirectionalRanges = CreateDirectionalRangesFromAxes();
        else
            DirectionalRanges = DirectionalRanges.Select(radius =>
                Math.Clamp(radius, 256, MaximumRadius)).ToArray();
    }

    private static int[] CreateDefaultDirectionalRanges() =>
        Enumerable.Repeat(1500, 8).ToArray();

    private int[] CreateDirectionalRangesFromAxes()
    {
        var ranges = new int[8];
        for (var index = 0; index < ranges.Length; index++)
        {
            var angle = index * Math.PI / 4;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var xRadius = cos >= 0 ? MaximumX - CenterX : CenterX - MinimumX;
            var yRadius = sin >= 0 ? MaximumY - CenterY : CenterY - MinimumY;
            var inverseSquared = cos * cos / (xRadius * xRadius) + sin * sin / (yRadius * yRadius);
            ranges[index] = Math.Clamp((int)Math.Round(1 / Math.Sqrt(inverseSquared)), 256, MaximumRadius);
        }
        return ranges;
    }
}
