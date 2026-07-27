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

        controllerInput = new ProControllerInput(interop, settings);
        settingsWindow = new SettingsWindow(settings, controllerInput,
            () => Save(settingsPath, settings), () => pluginInterface.UiLanguage);
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

    private readonly PluginSettings settings;
    private readonly ProControllerInput controller;
    private readonly Action save;
    private readonly Func<string> language;

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

        ImGui.Separator();
        var changed = false;
        var enabled = settings.Enabled;
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
        changed |= ImGui.Checkbox(T("swapAb"), ref swapAb);
        changed |= ImGui.Checkbox(T("swapXy"), ref swapXy);
        changed |= ImGui.SliderFloat(T("leftDeadzone"), ref leftDeadzone, 0f, .5f, "%.2f");
        changed |= ImGui.SliderFloat(T("rightDeadzone"), ref rightDeadzone, 0f, .5f, "%.2f");
        settings.Enabled = enabled;
        settings.SwapAb = swapAb;
        settings.SwapXy = swapXy;
        settings.LeftDeadzone = leftDeadzone;
        settings.RightDeadzone = rightDeadzone;
        if (changed) save();

        ImGui.Spacing();
        ImGui.TextDisabled(T("command"));
    }

    private string T(string key) => LocalizedText.Get(
        string.IsNullOrWhiteSpace(settings.Language) || settings.Language == "auto"
            ? language()
            : settings.Language,
        key);
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
        ["language"] = ["界面语言", "Interface language", "Oberflächensprache", "Langue de l’interface", "介面語言", "인터페이스 언어", "表示言語"],
        ["autoLanguage"] = ["跟随游戏", "Follow game language", "Spielsprache verwenden", "Suivre la langue du jeu", "跟隨遊戲", "게임 언어 따르기", "ゲーム言語に合わせる"],
        ["enabled"] = ["启用手柄适配", "Enable controller support", "Controller-Unterstützung aktivieren", "Activer la prise en charge de la manette", "啟用控制器適配", "컨트롤러 지원 활성화", "コントローラー対応を有効にする"],
        ["swapAb"] = ["交换 A / B", "Swap A / B", "A / B tauschen", "Inverser A / B", "交換 A / B", "A / B 교체", "A / B を入れ替える"],
        ["swapXy"] = ["交换 X / Y", "Swap X / Y", "X / Y tauschen", "Inverser X / Y", "交換 X / Y", "X / Y 교체", "X / Y を入れ替える"],
        ["leftDeadzone"] = ["左摇杆死区", "Left stick deadzone", "Totzone linker Stick", "Zone morte du stick gauche", "左搖桿死區", "왼쪽 스틱 데드존", "左スティックのデッドゾーン"],
        ["rightDeadzone"] = ["右摇杆死区", "Right stick deadzone", "Totzone rechter Stick", "Zone morte du stick droit", "右搖桿死區", "오른쪽 스틱 데드존", "右スティックのデッドゾーン"],
        ["command"] = ["设置命令：/npro", "Settings command: /npro", "Einstellungsbefehl: /npro", "Commande des paramètres : /npro", "設定指令：/npro", "설정 명령어: /npro", "設定コマンド：/npro"],
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
    public bool SwapAb { get; set; }
    public bool SwapXy { get; set; }
    public float LeftDeadzone { get; set; } = .35f;
    public float RightDeadzone { get; set; } = .35f;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PluginSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path), JsonOptions) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
}
