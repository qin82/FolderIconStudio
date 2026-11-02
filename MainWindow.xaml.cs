using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderIconStudio.Services;
using Microsoft.Win32;

namespace FolderIconStudio;

public partial class MainWindow : Window
{
    private readonly IconLibraryService _library;
    private readonly ObservableCollection<FolderTarget> _folders = [];
    private bool _switchingLibrary;

    public MainWindow(IconLibraryService library)
    {
        InitializeComponent();
        _library = library;
        FolderList.ItemsSource = _folders;

        _switchingLibrary = true;
        LibraryPicker.ItemsSource = library.Groups;
        LibraryPicker.SelectedItem = library.Current;
        _switchingLibrary = false;
        BindCurrentLibrary();

        UpdateState();
    }

    private void BindCurrentLibrary()
    {
        IconList.ItemsSource = _library.Current.Icons;
        IconList.SelectedIndex = _library.Current.Icons.Count > 0 ? 0 : -1;
    }

    private void LibraryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingLibrary || LibraryPicker.SelectedItem is not IconGroup group) return;
        if (ReferenceEquals(group, _library.Current)) return;

        _library.SwitchTo(group);
        BindCurrentLibrary();
        StatusText.Text = $"已切换到「{group.Name}」，本库 {group.Icons.Count} 个图标。";
        UpdateState();
    }

    private void NewLibrary_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "新建图标库", "图标库名称：", string.Empty);
        while (name is not null)
        {
            var normalized = IconLibraryService.NormalizeGroupName(name);
            if (normalized is null)
            {
                MessageBox.Show(this, "图标库名称不能为空。", "新建图标库",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!_library.IsNameAvailable(normalized))
            {
                MessageBox.Show(this, "这个名称已经被其他图标库使用了，请换一个。", "新建图标库",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var group = _library.AddGroup(normalized);
                ShowLibrary(group);
                StatusText.Text = $"已新建「{group.Name}」，可以导入图标。";
                UpdateState();
                return;
            }

            name = TextPromptWindow.Show(this, "新建图标库", "图标库名称：", name);
        }
    }

    private void RenameLibrary_Click(object sender, RoutedEventArgs e)
    {
        var group = _library.Current;
        var name = TextPromptWindow.Show(this, "重命名图标库", "图标库名称：", group.Name);
        if (name is null) return;

        if (!_library.RenameGroup(group, name))
        {
            MessageBox.Show(this, "这个名称已经被其他图标库使用了。", "重命名图标库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = $"图标库已重命名为「{group.Name}」。";
    }

    private void DeleteLibrary_Click(object sender, RoutedEventArgs e)
    {
        var group = _library.Current;
        if (_library.Groups.Count <= 1)
        {
            MessageBox.Show(this, "至少要保留一个图标库。", "删除图标库",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"确定删除「{group.Name}」吗？\n其中的 {group.Icons.Count} 个图标会从图标库删除，已经应用到文件夹的图标不受影响。",
            "删除图标库", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_library.RemoveGroup(group)) return;
        ShowLibrary(_library.Current);
        StatusText.Text = $"已删除「{group.Name}」。";
        UpdateState();
    }

    private void ShowLibrary(IconGroup group)
    {
        _switchingLibrary = true;
        LibraryPicker.SelectedItem = group;
        _switchingLibrary = false;
        BindCurrentLibrary();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图标或图片（可多选）",
            Multiselect = true,
            Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp|ICO 图标|*.ico|图片|*.png;*.jpg;*.jpeg;*.bmp"
        };

        if (dialog.ShowDialog(this) == true) ImportFiles(dialog.FileNames);
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        var icons = _library.Current.Icons;
        var before = icons.Count;
        SetBusy(true, "正在导入图标...");
        try
        {
            var report = _library.Import(paths);

            if (icons.Count > 0 && IconList.SelectedItem is null) IconList.SelectedIndex = 0;
            if (icons.Count > before && IconList.SelectedIndex >= 0 && report.Added > 0)
            {
                IconList.SelectedIndex = icons.Count - 1;
                IconList.ScrollIntoView(IconList.SelectedItem);
            }

            var message = $"已导入 {report.Added} 个图标到「{_library.Current.Name}」";
            if (report.Skipped > 0) message += $"，跳过 {report.Skipped} 个重复项";
            StatusText.Text = message + "。";
            if (report.Errors.Count > 0) ShowErrors("部分文件导入失败", report.Errors);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            UpdateState();
        }
    }

    private void RemoveIcon_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconEntry entry) return;

        _library.Remove(entry);
        if (_library.Current.Icons.Count > 0) IconList.SelectedIndex = 0;
        StatusText.Text = $"已从「{_library.Current.Name}」移除「{entry.Name}」，已应用图标的文件夹不受影响。";
        UpdateState();
    }

    private void AddFolders_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要设置图标的文件夹",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true) AddFolders(dialog.FolderNames);
    }

    private void AddFolders(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) continue;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (_folders.Any(existing => string.Equals(existing.Path, full, StringComparison.OrdinalIgnoreCase))) continue;

            _folders.Add(new FolderTarget(full));
            added++;
        }

        if (added > 0) StatusText.Text = $"已添加 {added} 个文件夹。";
        UpdateState();
    }

    private void ClearFolders_Click(object sender, RoutedEventArgs e)
    {
        _folders.Clear();
        StatusText.Text = "文件夹列表已清空（不会改变已应用的图标）。";
        UpdateState();
    }

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;

        var selected = FolderList.SelectedItems.Cast<FolderTarget>().ToList();
        foreach (var folder in selected) _folders.Remove(folder);
        if (selected.Count > 0) StatusText.Text = $"已从列表移除 {selected.Count} 个文件夹。";
        UpdateState();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconEntry icon)
        {
            MessageBox.Show(this, "请先在图标库里选择一个图标。", "文件夹图标设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_folders.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一个文件夹。", "文件夹图标设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folders = _folders.Select(folder => folder.Path).ToArray();
        var recursive = RecursiveCheck.IsChecked == true;
        SetBusy(true, "正在应用图标...");

        try
        {
            var report = await Task.Run(() => FolderIconService.Apply(folders, icon.FilePath, recursive));
            StatusText.Text = report.Failed == 0
                ? $"已为 {report.Applied} 个文件夹应用图标「{icon.Name}」。"
                : $"完成：成功 {report.Applied} 个，失败 {report.Failed} 个。";
            if (report.HasErrors) ShowErrors("部分文件夹处理失败", report.Errors);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            UpdateState();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) return;

        var recursive = RecursiveCheck.IsChecked == true;
        var confirm = MessageBox.Show(this,
            $"确定要恢复列表中 {_folders.Count} 个文件夹的默认图标吗？\n文件夹里的其他内容不会被删除。",
            "恢复默认图标", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var folders = _folders.Select(folder => folder.Path).ToArray();
        SetBusy(true, "正在恢复默认图标...");

        try
        {
            var report = await Task.Run(() => FolderIconService.Clear(folders, recursive));
            StatusText.Text = report.Failed == 0
                ? $"已恢复 {report.Applied} 个文件夹的默认图标。"
                : $"完成：恢复 {report.Applied} 个，失败 {report.Failed} 个。";
            if (report.HasErrors) ShowErrors("部分文件夹处理失败", report.Errors);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            UpdateState();
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) return;

        var folders = _folders.Select(folder => folder.Path).ToArray();
        var recursive = RecursiveCheck.IsChecked == true;
        SetBusy(true, "正在修复图标属性...");

        try
        {
            var report = await Task.Run(() => FolderIconService.Repair(folders, recursive));
            StatusText.Text = report.Applied == 0
                ? "没有找到由本程序设置过图标的文件夹。"
                : $"已修复 {report.Applied} 个文件夹的图标属性。";
            if (report.HasErrors) ShowErrors("部分文件夹处理失败", report.Errors);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            UpdateState();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        AddFolders(paths.Where(Directory.Exists));
        var files = paths.Where(path => File.Exists(path) && IconLibraryService.IsSupported(path)).ToArray();
        if (files.Length > 0) ImportFiles(files);
    }

    private void UpdateState()
    {
        var icons = _library.Current.Icons;
        LibraryCountText.Text = _library.Groups.Count > 1
            ? $"本库 {icons.Count} 个图标 · 全部 {_library.TotalCount} 个"
            : $"共 {icons.Count} 个图标";
        EmptyLibraryText.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyFoldersText.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteLibraryButton.IsEnabled = _library.Groups.Count > 1;

        var hasIcon = icons.Count > 0;
        var hasFolder = _folders.Count > 0;
        FolderCountText.Text = hasFolder ? $"{_folders.Count} 个文件夹" : "尚未添加";
        ClearFoldersButton.IsEnabled = hasFolder;
        ApplyButton.IsEnabled = hasIcon && hasFolder;
        RestoreButton.IsEnabled = hasFolder;
        RepairButton.IsEnabled = hasFolder;
        IconList.IsEnabled = hasIcon;
    }

    private void SetBusy(bool busy, string status)
    {
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
        IsEnabled = !busy;
        if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
    }

    private void ShowErrors(string title, IReadOnlyList<string> errors)
    {
        var lines = errors.Take(10).ToList();
        if (errors.Count > lines.Count) lines.Add($"……共 {errors.Count} 条");
        MessageBox.Show(this, string.Join(Environment.NewLine, lines), title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

public sealed class FolderTarget
{
    public FolderTarget(string path)
    {
        Path = path;
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        Name = System.IO.Path.GetFileName(trimmed);
        if (Name.Length == 0) Name = trimmed;
    }

    public string Path { get; }
    public string Name { get; }

    public override string ToString() => Path;
}
