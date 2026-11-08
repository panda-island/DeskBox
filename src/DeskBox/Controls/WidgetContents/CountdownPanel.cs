using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DeskBox.Controls.WidgetContents;

public sealed class CountdownEntry
{
    public string Title { get; set; } = "";
    public DateTime Target { get; set; }
}

public sealed class CountdownPreferences
{
    public bool Visible { get; set; }
    public bool? DesktopMode { get; set; }
    public List<CountdownEntry> Entries { get; set; } = [];
}

[JsonSerializable(typeof(CountdownPreferences))]
internal sealed partial class CountdownJsonContext : JsonSerializerContext { }

public sealed partial class CountdownPanel : StackPanel
{
    private readonly string _path;
    private readonly StackPanel _list = new() { Spacing = 20 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CountdownPreferences _data = new();
    private bool _busy;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public bool IsCountdownVisible => _data.DesktopMode ?? (_data.Visible || _data.Entries.Count > 0);
    public event Action? DisplayChanged;

    public CountdownPanel(string widgetId)
    {
        _path = Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "countdowns", Path.GetFileName(widgetId) + ".json");
        Spacing = 12;
        Margin = new Thickness(16, 46, 16, 16);
        Children.Add(_list);
        var add = new Button { Content = "＋ 新增倒數", HorizontalAlignment = HorizontalAlignment.Center };
        add.Click += async (_, _) => await EditAsync(null);
        Children.Add(add);
        Children.Add(_status);
        _timer.Tick += (_, _) => Render();
        Loaded += (_, _) => { Render(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        try
        {
            if (File.Exists(_path))
                _data = JsonSerializer.Deserialize(File.ReadAllText(_path), CountdownJsonContext.Default.CountdownPreferences) ?? new();
            _data.Entries ??= [];
        }
        catch (Exception ex) { _status.Text = "無法讀取倒數設定：" + ex.Message; }
        Render();
    }

    public async Task SetVisibleAsync(bool visible)
    {
        _data.Visible = visible;
        _data.DesktopMode = visible;
        DisplayChanged?.Invoke();
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_data, CountdownJsonContext.Default.CountdownPreferences);
            await ResilientJsonStore.SaveAsync(_path, json);
            _status.Text = "";
        }
        catch (Exception ex) { _status.Text = "儲存失敗，請重試：" + ex.Message; }
        finally { _saveGate.Release(); }
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_data.Entries.Count == 0)
            _list.Children.Add(new TextBlock { Text = "新增成年、統測或期待的日子", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var entry in _data.Entries.OrderBy(e => e.Target))
        {
            int days = (entry.Target.Date - DateTime.Today).Days;
            var row = new StackPanel { Spacing = 4 };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = entry.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var edit = new Button { Content = "⋯", Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(8, 2, 8, 2) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, "編輯「" + entry.Title + "」倒數");
            edit.Click += async (_, _) => await EditAsync(entry);
            Grid.SetColumn(edit, 1);
            heading.Children.Add(title); heading.Children.Add(edit);
            row.Children.Add(heading);
            var number = new TextBlock { Text = days == 0 ? "就是今天！" : $"{Math.Abs(days):N0} 天", FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            row.Children.Add(new Viewbox { Child = number, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, StretchDirection = Microsoft.UI.Xaml.Controls.StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 62 });
            row.Children.Add(new TextBlock { Text = (days < 0 ? "已過 · " : days > 0 ? "還有 · " : "") + entry.Target.ToString("yyyy/MM/dd"), FontSize = 12 });
            _list.Children.Add(row);
        }
    }

    private async Task EditAsync(CountdownEntry? entry)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var editor = new CountdownEditorWindow(entry, XamlRoot?.RasterizationScale ?? 1);
            editor.Activate();
            var result = await editor.Completion;
            if (result.Deleted && entry is not null) _data.Entries.Remove(entry);
            else if (result.Value is { } value)
            {
                if (entry is null) _data.Entries.Add(value);
                else { entry.Title = value.Title; entry.Target = value.Target; }
                _data.Visible = true;
                _data.DesktopMode = true;
            }
            else return;
            Render();
            DisplayChanged?.Invoke();
            await SaveAsync();
        }
        finally { _busy = false; }
    }
}

/// <summary>The editor owns a normal desktop window, independent of the widget's bounds.</summary>
internal sealed partial class CountdownEditorWindow : Window
{
    private readonly TaskCompletionSource<(CountdownEntry? Value, bool Deleted)> _completion = new();
    public Task<(CountdownEntry? Value, bool Deleted)> Completion => _completion.Task;

    public CountdownEditorWindow(CountdownEntry? entry, double scale)
    {
        Title = entry is null ? "新增倒數" : "編輯倒數";
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 20 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBox { Header = "名稱", Text = entry?.Title ?? "統測", MaxLength = 80, HorizontalAlignment = HorizontalAlignment.Stretch };
        var kind = new ComboBox { Header = "類型", ItemsSource = new[] { "自訂日期／統測", "成年（出生日期 ＋ 18 年）" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var date = new CalendarDatePicker { Header = "目標日期", MinDate = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero), MaxDate = new DateTimeOffset(2200, 12, 31, 0, 0, 0, TimeSpan.Zero), Date = entry is null ? null : new DateTimeOffset(entry.Target), HorizontalAlignment = HorizontalAlignment.Stretch };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        kind.SelectionChanged += (_, _) => { date.Header = kind.SelectedIndex == 1 ? "出生日期" : "目標日期"; if (entry is null) title.Text = kind.SelectedIndex == 1 ? "成年" : "統測"; };
        var body = new StackPanel { Spacing = 20 };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 24 });
        body.Children.Add(kind); body.Children.Add(title); body.Children.Add(date);
        body.Children.Add(new TextBlock { Text = "成年以出生日期加 18 年計算；統測請選擇你的考試日期。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(error);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "儲存" };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || date.Date is null || (kind.SelectedIndex == 1 && date.Date.Value.Date > DateTime.Today))
            { error.Text = "請填寫名稱與有效日期；出生日期不能晚於今天。"; return; }
            var target = date.Date.Value.Date;
            if (kind.SelectedIndex == 1) target = target.AddYears(18);
            _completion.TrySetResult((new CountdownEntry { Title = title.Text.Trim(), Target = target }, false));
            Close();
        };
        if (entry is not null)
        {
            var delete = new Button { Content = "刪除" };
            bool confirming = false;
            delete.Click += (_, _) => { if (!confirming) { confirming = true; delete.Content = "確定刪除"; return; } _completion.TrySetResult((null, true)); Close(); };
            actions.Children.Add(delete);
        }
        Grid.SetColumn(cancel, 1); Grid.SetColumn(save, 2);
        actions.Children.Add(cancel); actions.Children.Add(save);
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        Content = root;
        WindowsCompatibilityService.ApplySafeBackdrop(this);
        App.Current.ThemeService.TrackWindow(this);
        AppBranding.ApplyWindowIcon(AppWindow);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(CountdownEditorLayout.GetBounds(area, scale));
        Closed += (_, _) => _completion.TrySetResult((null, false));
    }
}

internal static class CountdownEditorLayout
{
    internal static RectInt32 GetBounds(RectInt32 area, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        int width = Math.Min((int)Math.Min(480 * scale, int.MaxValue), Math.Max(1, area.Width - 32));
        int height = Math.Min((int)Math.Min(620 * scale, int.MaxValue), Math.Max(1, area.Height - 32));
        return new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }
}