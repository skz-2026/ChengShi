using System.Windows;
using System.Windows.Controls;
using Chengshi.Core;
using Chengshi.Engine;
using Microsoft.Win32;

namespace Chengshi.App;

public partial class AppsWindow : Window
{
    private static IReadOnlyList<InstalledApp>? _catalogCache;

    private readonly IReadOnlyList<AllowedApp> _seed;
    private List<AppPickItem> _items = [];

    public AppsWindow(IReadOnlyList<AllowedApp> selected)
    {
        _seed = selected;
        InitializeComponent();
        AppIcon.Apply(this);
        Loaded += OnLoaded;
    }

    public IReadOnlyList<AllowedApp>? Result { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var catalog = _catalogCache ?? await Task.Run(InstalledAppCatalog.Scan);
            _catalogCache = catalog;
            if (!IsLoaded)
            {
                return;
            }

            Merge(catalog);
            ApplyFilter();
        }
        catch (Exception)
        {
            Merge([]);
            ApplyFilter();
            StatusText.Text = "没能扫到开始菜单，仍可从文件添加，或勾选正在运行的程序。";
        }
    }

    private void Merge(IReadOnlyList<InstalledApp> catalog)
    {
        var unmatched = _seed.ToList();
        var items = new List<AppPickItem>();

        foreach (var app in catalog)
        {
            var match = unmatched.FirstOrDefault(s =>
                string.Equals(s.FileName, app.FileName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(s.ImagePath)
                    && string.Equals(s.ImagePath, app.ImagePath, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                unmatched.Remove(match);
                items.Add(new AppPickItem
                {
                    IsChecked = true,
                    App = app with { DisplayName = match.DisplayName },
                    DailyMinutes = match.DailyMinutes,
                });
            }
            else
            {
                items.Add(new AppPickItem { App = app });
            }
        }

        foreach (var leftover in unmatched)
        {
            items.Add(new AppPickItem
            {
                IsChecked = true,
                App = new InstalledApp(leftover.DisplayName, leftover.FileName, leftover.ImagePath, "已选"),
                DailyMinutes = leftover.DailyMinutes,
            });
        }

        _items = items
            .OrderByDescending(i => i.IsChecked)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<AppPickItem> view = _items;
        if (query.Length > 0)
        {
            view = _items.Where(i =>
                i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.App.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = view.ToList();
        AppList.ItemsSource = filtered;
        var checkedCount = _items.Count(i => i.IsChecked);
        StatusText.Text = _items.Count == 0
            ? "还没有找到软件。点左下角从文件添加。"
            : $"本机 { _items.Count } 款，已勾选 {checkedCount} 款。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "程序 (*.exe)|*.exe",
            Title = "选择允许打开的程序",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var added = InstalledAppCatalog.FromExecutable(dialog.FileName);
        var existing = _items.FirstOrDefault(i =>
            string.Equals(i.Key, added.ImagePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.App.FileName, added.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsChecked = true;
        }
        else
        {
            _items.Insert(0, new AppPickItem { IsChecked = true, App = added });
        }

        SearchBox.Text = string.Empty;
        ApplyFilter();
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        Result = _items
            .Where(i => i.IsChecked)
            .Select(i => i.App.ToAllowed(i.DailyMinutes))
            .ToArray();
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed class AppPickItem
{
    public bool IsChecked { get; set; }
    public required InstalledApp App { get; init; }

    /// <summary>种子名单里已有的单软件限时，勾选完成时原样带回。</summary>
    public int? DailyMinutes { get; init; }

    public string Title => App.DisplayName;
    public string Subtitle => $"{App.Source}  ·  {App.FileName}";
    public string Key => App.ImagePath ?? App.FileName;
}
