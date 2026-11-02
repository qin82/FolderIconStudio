using System.IO;
using System.Windows;
using FolderIconStudio.Services;

namespace FolderIconStudio;

public partial class App : Application
{
    private SingleInstance? _instance;
    private IconLibraryService? _library;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var apply = ReadApplyRequest(e.Args);
        if (apply is not null)
        {
            ApplyFromMenu(apply.Folder, apply.IconPath);
            Shutdown();
            return;
        }

        var clearFolder = ReadFolderOption(e.Args, "--clear-icon");
        if (clearFolder is not null)
        {
            RestoreDefaultIcon(clearFolder);
            Shutdown();
            return;
        }

        var folder = ReadFolderArgument(e.Args);
        _instance = new SingleInstance();

        if (!_instance.IsPrimary)
        {
            var forwarded = folder is not null
                ? SingleInstance.TrySend("icon|" + folder)
                : SingleInstance.TrySend("open|");

            if (!forwarded && folder is not null)
            {
                // The primary instance is gone but its menu entry lingered: serve this click here.
                _library = new IconLibraryService();
                ShowQuickApply(folder);
            }
            else
            {
                Shutdown();
            }

            return;
        }

        _library = new IconLibraryService();
        _library.Changed += RebuildMenus;
        _instance.FolderRequested += path => Dispatcher.BeginInvoke(() => ShowQuickApply(path));
        _instance.OpenRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);

        var executable = Environment.ProcessPath ?? string.Empty;
        if (!ShellMenuService.Register(executable, _library))
        {
            MessageBox.Show(
                "右键菜单注册失败，主窗口仍可正常使用。",
                "文件夹图标设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (folder is not null) ShowQuickApply(folder);
        else ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_library is null) return;

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_library);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            _mainWindow.Show();
        }
        else
        {
            if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    private void ShowQuickApply(string folder)
    {
        if (_library is null || !Directory.Exists(folder)) return;

        var window = new QuickApplyWindow(_library, folder);
        if (_mainWindow is { IsVisible: true }) window.Owner = _mainWindow;
        window.Show();
        window.Activate();
    }

    private static string? ReadFolderArgument(string[] args) => ReadFolderOption(args, "--set-icon");

    private void RebuildMenus()
    {
        if (_library is null) return;
        ShellMenuService.Register(Environment.ProcessPath ?? string.Empty, _library);
    }

    private sealed record ApplyIconRequest(string Folder, string IconPath);

    private static ApplyIconRequest? ReadApplyRequest(string[] args)
    {
        for (var i = 0; i < args.Length - 2; i++)
        {
            if (args[i].Equals("--apply-icon", StringComparison.OrdinalIgnoreCase))
            {
                return new ApplyIconRequest(args[i + 1], args[i + 2]);
            }
        }

        return null;
    }

    private static void ApplyFromMenu(string folder, string iconPath)
    {
        if (!Directory.Exists(folder) || !File.Exists(iconPath))
        {
            MessageBox.Show("文件夹或图标已不存在，请重新设置。", "设置文件夹图标失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var report = FolderIconService.Apply([folder], iconPath, recursive: false);
            if (!report.HasErrors) return;

            MessageBox.Show(string.Join(Environment.NewLine, report.Errors.Take(10)),
                "设置文件夹图标失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "设置文件夹图标失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? ReadFolderOption(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                return Directory.Exists(value) ? value : null;
            }
        }

        return null;
    }

    private static void RestoreDefaultIcon(string folder)
    {
        try
        {
            var report = FolderIconService.Clear([folder], recursive: false);
            if (!report.HasErrors) return;

            MessageBox.Show(string.Join(Environment.NewLine, report.Errors.Take(10)),
                "恢复默认图标失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "恢复默认图标失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
