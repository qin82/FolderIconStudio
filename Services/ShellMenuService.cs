using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace FolderIconStudio.Services;

/// <summary>
/// Publishes per-user Explorer context menus. They stay registered after the app exits so
/// a menu click can start the app on demand; only HKCU is touched, so no admin rights are needed.
/// Explorer silently drops every item after the 16th one of a static cascade tree, so each icon
/// library gets its own top-level cascade and libraries larger than 15 icons are split into pages.
/// </summary>
internal static class ShellMenuService
{
    private const int MaxIconsPerPage = 15;

    private const string FolderKey = @"Software\Classes\Directory\shell\FolderIconStudio";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\FolderIconStudio";
    private const string FolderRestoreKey = @"Software\Classes\Directory\shell\FolderIconStudio.Restore";
    private const string BackgroundRestoreKey = @"Software\Classes\Directory\Background\shell\FolderIconStudio.Restore";

    public static bool Register(string executablePath, IconLibraryService library)
    {
        try
        {
            DeleteMenus();
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

            var menuIcon = EnsureMenuIcon();
            CreateMenu(FolderKey, "设置文件夹图标(&I)...", executablePath, "%1", "--set-icon", menuIcon);
            CreateMenu(BackgroundKey, "为当前文件夹设置图标(&I)...", executablePath, "%V", "--set-icon", menuIcon);
            CreateGroupMenus(@"Software\Classes\Directory\shell", @"Directory\shell",
                executablePath, "%1", library);
            CreateGroupMenus(@"Software\Classes\Directory\Background\shell", @"Directory\Background\shell",
                executablePath, "%V", library);
            CreateMenu(FolderRestoreKey, "恢复默认文件夹图标(&R)", executablePath, "%1", "--clear-icon", menuIcon);
            CreateMenu(BackgroundRestoreKey, "恢复默认文件夹图标(&R)", executablePath, "%V", "--clear-icon", menuIcon);
            ShellRefresh.RefreshMenus();
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Explorer caches a menu icon per file path, so pointing at the exe can keep showing the
    /// icon of an earlier build. Extracting the embedded icon under a content-hashed file name
    /// gives every build a fresh path and keeps the menu icon identical to the application icon.
    /// </summary>
    private static string? EnsureMenuIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app-icon.ico"));
            if (resource is null) return null;

            using var source = resource.Stream;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant();
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderIconStudio");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"app-icon.{hash}.ico");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void DeleteMenus()
    {
        DeleteOurKeys(@"Software\Classes\Directory\shell");
        DeleteOurKeys(@"Software\Classes\Directory\Background\shell");
    }

    private static void DeleteOurKeys(string parentPath)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
        if (parent is null) return;

        foreach (var name in parent.GetSubKeyNames())
        {
            if (!name.StartsWith("FolderIconStudio", StringComparison.OrdinalIgnoreCase)) continue;
            parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
    }

    private static void CreateGroupMenus(string shellRoot, string menuRoot,
        string executablePath, string folderToken, IconLibraryService library)
    {
        var pageIndex = 0;

        foreach (var group in library.Groups)
        {
            if (group.Icons.Count == 0) continue;

            var pageCount = (group.Icons.Count + MaxIconsPerPage - 1) / MaxIconsPerPage;
            var baseSize = group.Icons.Count / pageCount;
            var remainder = group.Icons.Count % pageCount;
            var offset = 0;

            for (var page = 0; page < pageCount; page++)
            {
                var size = baseSize + (page < remainder ? 1 : 0);
                var icons = group.Icons.Skip(offset).Take(size).ToList();
                offset += size;

                var keyName = $"FolderIconStudio.G{pageIndex:D3}";
                var label = pageCount == 1 ? group.Name : $"{group.Name} ({page + 1}/{pageCount})";
                CreateIconPage($@"{shellRoot}\{keyName}", $@"{menuRoot}\{keyName}",
                    label, executablePath, folderToken, icons);
                pageIndex++;
            }
        }
    }

    private static void CreateIconPage(string keyPath, string menuPath, string label,
        string executablePath, string folderToken, IReadOnlyList<IconEntry> icons)
    {
        using var root = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException("无法创建右键菜单注册表项。");
        root.SetValue("MUIVerb", label, RegistryValueKind.String);
        root.SetValue("Icon", $"\"{icons[0].FilePath}\",0", RegistryValueKind.String);
        root.SetValue("ExtendedSubCommandsKey", menuPath, RegistryValueKind.String);

        using var shell = root.CreateSubKey("shell", writable: true)
            ?? throw new IOException("无法创建图标菜单组。");

        for (var i = 0; i < icons.Count; i++)
        {
            var icon = icons[i];
            using var iconKey = shell.CreateSubKey($"i{i:D3}", writable: true)
                ?? throw new IOException("无法创建图标菜单项。");
            iconKey.SetValue("MUIVerb", icon.Name, RegistryValueKind.String);
            iconKey.SetValue("Icon", $"\"{icon.FilePath}\",0", RegistryValueKind.String);

            using var command = iconKey.CreateSubKey("command", writable: true)
                ?? throw new IOException("无法创建图标菜单命令。");
            command.SetValue(string.Empty,
                $"\"{executablePath}\" --apply-icon \"{folderToken}\" \"{icon.FilePath}\"", RegistryValueKind.String);
        }
    }

    private static void CreateMenu(string keyPath, string text, string executablePath,
        string folderToken, string verb, string? menuIcon)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException("无法创建右键菜单注册表项。");
        key.SetValue("MUIVerb", text, RegistryValueKind.String);
        key.SetValue("Icon", IconValue(menuIcon, executablePath), RegistryValueKind.String);

        using var command = key.CreateSubKey("command", writable: true)
            ?? throw new IOException("无法创建右键菜单命令项。");
        command.SetValue(string.Empty, $"\"{executablePath}\" {verb} \"{folderToken}\"", RegistryValueKind.String);
    }

    private static string IconValue(string? menuIcon, string executablePath) =>
        $"\"{menuIcon ?? executablePath}\",0";
}
