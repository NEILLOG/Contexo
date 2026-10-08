using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace ImePoc;

public sealed partial class MainWindow : Window
{
    // 正式程式預計使用的字型順序：Windows 微軟正黑體 → macOS 蘋方 → Linux Noto
    private const string CjkFontFamily = "Microsoft JhengHei UI, Microsoft JhengHei, PingFang TC, Noto Sans CJK TC, Noto Sans TC";

    private readonly TextBox _log;
    private readonly ScaleTransform _scale;
    private readonly TextBlock _status;
    private readonly Border _overlay;
    private readonly FontFamily _defaultFont;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _log = Get<TextBox>("LogBox");
        _scale = new ScaleTransform(1, 1);
        Get<LayoutTransformControl>("ScaleHost").LayoutTransform = _scale;
        _status = Get<TextBlock>("StatusLine");
        _overlay = Get<Border>("Overlay");
        _defaultFont = FontFamily;

        // 清單內的三個輸入框
        var panel = Get<StackPanel>("ItemsHost");
        foreach (var label in new[] { "資料夾名稱", "排除的檔名", "備註" })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var box = new TextBox { Name = "Item_" + label, Width = 320 };
            Track(box, label);
            row.Children.Add(box);
            panel.Children.Add(row);
        }

        Track(Get<TextBox>("SearchBox"), "搜尋框", submitOnEnter: true);
        Track(Get<TextBox>("MultiLineBox"), "多行");
        Track(Get<TextBox>("OverlayBox"), "對話框");

        Get<Button>("ThemeSystem").Click += (_, _) => SetTheme(ThemeVariant.Default, "跟隨系統");
        Get<Button>("ThemeLight").Click += (_, _) => SetTheme(ThemeVariant.Light, "淺色");
        Get<Button>("ThemeDark").Click += (_, _) => SetTheme(ThemeVariant.Dark, "深色");
        Get<Button>("Scale100").Click += (_, _) => SetScale(1.0);
        Get<Button>("Scale112").Click += (_, _) => SetScale(1.12);
        Get<Button>("Scale125").Click += (_, _) => SetScale(1.25);
        Get<Button>("FontCjk").Click += (_, _) => SetFont(true);
        Get<Button>("FontDefault").Click += (_, _) => SetFont(false);
        Get<Button>("OpenOverlay").Click += (_, _) => { _overlay.IsVisible = true; Get<TextBox>("OverlayBox").Focus(); };
        Get<Button>("CloseOverlay").Click += (_, _) => _overlay.IsVisible = false;
        Get<Button>("ClearLog").Click += (_, _) => _log.Text = string.Empty;

        SetFont(true);
        UpdateStatus();
    }

    private T Get<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException("找不到控制項：" + name);

    private void Track(TextBox box, string label, bool submitOnEnter = false)
    {
        // 文字確定送入（IME 選字完成、直接打英數）時觸發
        box.AddHandler(TextInputEvent, (_, e) =>
            Log($"[{label}] 輸入確定：「{e.Text}」 {CodePoints(e.Text)}"), RoutingStrategies.Tunnel);

        // 按鍵：記錄 Enter / Backspace / Escape，觀察選字期間是否被誤觸
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Back or Key.Escape)
            {
                Log($"[{label}] 按鍵：{e.Key}");
            }

            if (submitOnEnter && e.Key == Key.Enter)
            {
                Log($"[{label}] ★ 送出搜尋：「{box.Text}」");
            }
        }, RoutingStrategies.Tunnel);

        // 文字內容變更：檢查是否有重複字或殘留注音符號
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                var text = box.Text ?? string.Empty;
                var warn = ContainsBopomofo(text) ? "  ⚠ 內容含注音符號（可能是組字殘留）" : string.Empty;
                Log($"[{label}] 目前內容：「{text}」（{new StringInfo(text).LengthInTextElements} 字）{warn}");
            }
        };
    }

    private void SetTheme(ThemeVariant variant, string name)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;
        }

        Log($"切換主題：{name}（實際：{ActualThemeVariant}）");
        UpdateStatus();
    }

    private void SetScale(double value)
    {
        _scale.ScaleX = value;
        _scale.ScaleY = value;
        Log($"切換字級：{value}");
        UpdateStatus();
    }

    private void SetFont(bool cjk)
    {
        FontFamily = cjk ? new FontFamily(CjkFontFamily) : _defaultFont;
        Log("切換字型：" + (cjk ? CjkFontFamily : "框架預設"));
        UpdateStatus();
    }

    private void UpdateStatus() =>
        _status.Text = $"{RuntimeInformation.OSDescription} · .NET {Environment.Version} · Avalonia {typeof(Application).Assembly.GetName().Version} · 主題 {ActualThemeVariant} · 字級 {_scale.ScaleX}";

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        _log.Text = line + Environment.NewLine + _log.Text;
    }

    private static bool ContainsBopomofo(string text)
    {
        foreach (var ch in text)
        {
            // U+3100–312F 注音符號、U+31A0–31BF 注音擴充；聲調符號 ˊˇˋ˙ 另外判斷
            if (ch is >= '㄀' and <= 'ㄯ' or >= 'ㆠ' and <= 'ㆿ' or 'ˊ' or 'ˇ' or 'ˋ' or '˙')
            {
                return true;
            }
        }

        return false;
    }

    private static string CodePoints(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            sb.Append($"U+{char.ConvertToUtf32(text, i):X4} ");
        }

        return sb.ToString().TrimEnd();
    }
}
