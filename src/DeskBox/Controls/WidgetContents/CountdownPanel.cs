using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed class CountdownEntry
{
    public string Title { get; set; } = "";
    public DateTime Target { get; set; }
}

public sealed class CountdownPreferences
{
    public bool Visible { get; set; }
    public List<CountdownEntry> Entries { get; set; } = [];
}

[JsonSerializable(typeof(CountdownPreferences))]
internal sealed partial class CountdownJsonContext : JsonSerializerContext { }

/// <summary>Local, per-widget countdowns, independent of photo/calendar preferences.</summary>
public sealed class CountdownPanel : StackPanel
{
    private readonly string _path;
    private readonly StackPanel _list = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CountdownPreferences _data = new();
    private bool _busy;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public bool IsCountdownVisible => _data.Visible;

    public CountdownPanel(string widgetId)
    {
        _path = Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "countdowns", Path.GetFileName(widgetId) + ".json");
        Spacing = 12;
        Margin = new Thickness(16, 48, 16, 16);
        Children.Add(new TextBlock { Text = "重要日子倒數", FontSize = 22 });
        Children.Add(new TextBlock { Text = "成年、統測，還有每個期待的日子", TextWrapping = TextWrapping.Wrap });
        var add = new Button { Content = "＋ 新增倒數" };
        add.Click += async (_, _) => await EditAsync(null);
        Children.Add(add);
        Children.Add(_status);
        Children.Add(_list);
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
            _list.Children.Add(new TextBlock { Text = "按「新增倒數」設定生日或考試日期。", TextWrapping = TextWrapping.Wrap });
        foreach (var entry in _data.Entries.OrderBy(e => e.Target))
        {
            int days = (entry.Target.Date - DateTime.Today).Days;
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = entry.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = days > 0 ? $"還有 {days:N0} 天" : days == 0 ? "就是今天！" : $"已過 {-days:N0} 天", FontSize = 28 });
            row.Children.Add(new TextBlock { Text = entry.Target.ToString("yyyy/MM/dd") });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var edit = new Button { Content = "編輯" };
            edit.Click += async (_, _) => await EditAsync(entry);
            var delete = new Button { Content = "刪除" };
            delete.Click += async (_, _) =>
            {
                if (_busy) return;
                _busy = true;
                try
                {
                    var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "刪除倒數？", Content = entry.Title, PrimaryButtonText = "刪除", CloseButtonText = "取消" };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    { _data.Entries.Remove(entry); await SaveAsync(); Render(); }
                }
                finally { _busy = false; }
            };
            actions.Children.Add(edit); actions.Children.Add(delete);
            row.Children.Add(actions);
            _list.Children.Add(new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = row });
        }
    }

    private async Task EditAsync(CountdownEntry? entry)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var title = new TextBox { Header = "名稱", Text = entry?.Title ?? "統測", MaxLength = 80 };
            var kind = new ComboBox { Header = "類型", ItemsSource = new[] { "自訂日期／統測", "成年（出生日期 ＋ 18 年）" }, SelectedIndex = 0 };
            var date = new CalendarDatePicker { Header = "目標日期", MinDate = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero), MaxDate = new DateTimeOffset(2200, 12, 31, 0, 0, 0, TimeSpan.Zero), Date = new DateTimeOffset(entry?.Target ?? DateTime.Today.AddDays(1)) };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            kind.SelectionChanged += (_, _) => { date.Header = kind.SelectedIndex == 1 ? "出生日期" : "目標日期"; if (entry is null) title.Text = kind.SelectedIndex == 1 ? "成年" : "統測"; };
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(kind); body.Children.Add(title); body.Children.Add(date); body.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = entry is null ? "新增倒數" : "編輯倒數", Content = body, PrimaryButtonText = "儲存", CloseButtonText = "取消" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || date.Date is null || (kind.SelectedIndex == 1 && date.Date.Value.Date > DateTime.Today))
                { args.Cancel = true; error.Text = "請填寫名稱與有效日期；出生日期不能晚於今天。"; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var target = date.Date!.Value.Date;
            if (kind.SelectedIndex == 1) target = target.AddYears(18);
            if (entry is null) { entry = new(); _data.Entries.Add(entry); }
            entry.Title = title.Text.Trim(); entry.Target = target;
            await SaveAsync(); Render();
        }
        finally { _busy = false; }
    }
}
