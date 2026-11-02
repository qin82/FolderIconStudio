using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderIconStudio.Services;
using Microsoft.Win32;

namespace FolderIconStudio;

public partial class QuickApplyWindow : Window
{
    private readonly IconLibraryService _library;
    private readonly string _folder;
    private bool _switchingLibrary;

    public QuickApplyWindow(IconLibraryService library, string folder)
    {
        InitializeComponent();
        _library = library;
        _folder = folder;

        FolderPathText.Text = folder;
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
        StatusText.Text = $"已切换到「{group.Name}」。";
        UpdateState();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图标或图片（可多选）",
            Multiselect = true,
            Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp|ICO 图标|*.ico|图片|*.png;*.jpg;*.jpeg;*.bmp"
        };

        if (dialog.ShowDialog(this) != true) return;

        Cursor = Cursors.Wait;
        try
        {
            var report = _library.Import(dialog.FileNames);
            if (_library.Current.Icons.Count > 0)
            {
                IconList.SelectedIndex = _library.Current.Icons.Count - 1;
                IconList.ScrollIntoView(IconList.SelectedItem);
            }

            var message = $"已导入 {report.Added} 个图标到「{_library.Current.Name}」";
            if (report.Skipped > 0) message += $"，跳过 {report.Skipped} 个重复项";
            StatusText.Text = message + "。";
            if (report.Errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, report.Errors.Take(10)),
                    "部分文件导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            Cursor = Cursors.Arrow;
            UpdateState();
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconEntry icon) return;

        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        StatusText.Text = "正在应用图标...";

        var report = await Task.Run(() => FolderIconService.Apply([_folder], icon.FilePath, recursive: false));

        Cursor = Cursors.Arrow;
        if (report.Failed == 0)
        {
            StatusText.Text = "已应用，正在刷新图标...";
            await Task.Delay(700);
            Close();
            return;
        }

        UpdateState();
        StatusText.Text = "应用失败。";
        MessageBox.Show(this, string.Join(Environment.NewLine, report.Errors.Take(10)),
            "应用图标失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "确定要恢复这个文件夹的默认图标吗？\n文件夹里的其他内容不会被删除。",
            "恢复默认图标", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        StatusText.Text = "正在恢复...";

        var report = await Task.Run(() => FolderIconService.Clear([_folder], recursive: false));

        Cursor = Cursors.Arrow;
        if (report.Failed == 0)
        {
            StatusText.Text = "已恢复默认图标。";
            await Task.Delay(700);
            Close();
            return;
        }

        UpdateState();
        MessageBox.Show(this, string.Join(Environment.NewLine, report.Errors.Take(10)),
            "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateState()
    {
        var count = _library.Current.Icons.Count;
        LibraryCountText.Text = $"{_library.Current.Name} · {count} 个图标";
        EmptyLibraryText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = count > 0;
        RestoreButton.IsEnabled = true;
    }
}
