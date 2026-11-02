using System.IO;
using System.Security.Cryptography;

namespace FolderIconStudio.Services;

public sealed record ApplyReport(int Applied, int Failed, IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Applies an icon that lives inside the folder itself and is referenced by a bare file name.
/// That relative reference is what keeps the icon working after a reinstall or on another PC.
/// The file name carries a hash of the image: Explorer only re-extracts a folder icon when the
/// desktop.ini reference changes, and a stable name would leave the previous bitmap cached.
/// </summary>
public static class FolderIconService
{
    private const string IconPrefix = ".foldericon.";
    private const string IconExtension = ".ico";

    private static string IconFileNameFor(byte[] iconBytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(iconBytes)).ToLowerInvariant();
        return $"{IconPrefix}{hash[..16]}{IconExtension}";
    }

    private static bool IsOurIconFile(string fileName) =>
        fileName.StartsWith(IconPrefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(IconExtension, StringComparison.OrdinalIgnoreCase);

    public static ApplyReport Apply(IReadOnlyList<string> folders, string sourceIcoPath, bool recursive)
    {
        var iconBytes = File.ReadAllBytes(sourceIcoPath);
        var targets = Expand(folders, recursive);
        var errors = new List<string>();
        var changed = new List<string>();

        foreach (var folder in targets)
        {
            try
            {
                ApplyToFolder(folder, iconBytes);
                changed.Add(folder);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                errors.Add($"{folder}：{exception.Message}");
            }
        }

        if (changed.Count > 0) ShellRefresh.RefreshFolders(changed);
        return new ApplyReport(changed.Count, errors.Count, errors);
    }

    public static ApplyReport Clear(IReadOnlyList<string> folders, bool recursive)
    {
        var targets = Expand(folders, recursive);
        var errors = new List<string>();
        var clearedFolders = new List<string>();

        foreach (var folder in targets)
        {
            try
            {
                if (ClearFromFolder(folder)) clearedFolders.Add(folder);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                errors.Add($"{folder}：{exception.Message}");
            }
        }

        if (clearedFolders.Count > 0) ShellRefresh.RefreshFolders(clearedFolders);
        return new ApplyReport(clearedFolders.Count, errors.Count, errors);
    }

    /// <summary>
    /// Re-arms the attributes Explorer needs after a folder was copied, restored from an archive
    /// or moved from another PC. Only folders that already contain our icon file are touched.
    /// </summary>
    public static ApplyReport Repair(IReadOnlyList<string> folders, bool recursive)
    {
        var targets = Expand(folders, recursive);
        var errors = new List<string>();
        var repairedFolders = new List<string>();

        foreach (var folder in targets)
        {
            try
            {
                var iniPath = Path.Combine(folder, DesktopIni.FileName);
                var reference = DesktopIni.GetIconFile(iniPath);
                if (reference is null) continue;

                var iconName = Path.GetFileName(reference);
                if (!IsOurIconFile(iconName)) continue;

                var iconPath = Path.Combine(folder, iconName);
                if (!File.Exists(iconPath)) continue;

                File.SetAttributes(iconPath, FileAttributes.Hidden | FileAttributes.System);
                File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
                var attributes = File.GetAttributes(folder);
                if ((attributes & FileAttributes.ReadOnly) == 0)
                {
                    File.SetAttributes(folder, attributes | FileAttributes.ReadOnly);
                }

                FolderCustomSettings.TrySetIcon(folder, iconName);

                repairedFolders.Add(folder);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                errors.Add($"{folder}：{exception.Message}");
            }
        }

        if (repairedFolders.Count > 0) ShellRefresh.RefreshFolders(repairedFolders);
        return new ApplyReport(repairedFolders.Count, errors.Count, errors);
    }

    public static IReadOnlyList<string> Expand(IReadOnlyList<string> roots, bool recursive)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                continue;
            }

            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) result.Add(full);
            if (!recursive) continue;

            // Iterative walk; reparse points are skipped so junctions cannot create cycles.
            var pending = new Stack<string>();
            pending.Push(full);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch (Exception exception) when (IsOperational(exception))
                        {
                            continue;
                        }

                        if (seen.Add(child))
                        {
                            result.Add(child);
                            pending.Push(child);
                        }
                    }
                }
                catch (Exception exception) when (IsOperational(exception))
                {
                    // An unreadable subtree is skipped; the folder itself is still handled.
                }
            }
        }

        return result;
    }

    private static void ApplyToFolder(string folder, byte[] iconBytes)
    {
        var iconName = IconFileNameFor(iconBytes);
        var iconPath = Path.Combine(folder, iconName);
        if (File.Exists(iconPath)) File.SetAttributes(iconPath, FileAttributes.Normal);
        File.WriteAllBytes(iconPath, iconBytes);
        File.SetAttributes(iconPath, FileAttributes.Hidden | FileAttributes.System);

        var folderAttributes = File.GetAttributes(folder);
        if ((folderAttributes & FileAttributes.ReadOnly) == 0)
        {
            File.SetAttributes(folder, folderAttributes | FileAttributes.ReadOnly);
        }

        var iniPath = Path.Combine(folder, DesktopIni.FileName);
        if (File.Exists(iniPath))
        {
            var iniAttributes = File.GetAttributes(iniPath);
            File.SetAttributes(iniPath, iniAttributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }

        if (!FolderCustomSettings.TrySetIcon(folder, iconName))
        {
            DesktopIni.SetIcon(iniPath, iconName);
        }

        if (File.Exists(iniPath)) File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);

        DeleteOtherIconFiles(folder, iconName);
    }

    private static void DeleteOtherIconFiles(string folder, string keepName)
    {
        foreach (var file in EnumerateIconFiles(folder))
        {
            if (string.Equals(Path.GetFileName(file), keepName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                // A leftover icon file is harmless.
            }
        }
    }

    private static IEnumerable<string> EnumerateIconFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, IconPrefix + "*", SearchOption.TopDirectoryOnly)
                .Where(file => IsOurIconFile(Path.GetFileName(file)))
                .ToList();
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            return [];
        }
    }

    private static bool ClearFromFolder(string folder)
    {
        var changed = false;
        var iniPath = Path.Combine(folder, DesktopIni.FileName);
        var iniRemoved = false;

        if (File.Exists(iniPath))
        {
            var attributes = File.GetAttributes(iniPath);
            File.SetAttributes(iniPath, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));

            // Drops the icon reference and repaints Explorer in one step.
            FolderCustomSettings.TryClearIcon(folder);

            iniRemoved = DesktopIni.ClearIcon(iniPath);
            if (iniRemoved)
            {
                if (File.Exists(iniPath)) File.Delete(iniPath);
                changed = true;
            }
            else
            {
                File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
            }
        }

        foreach (var iconPath in EnumerateIconFiles(folder))
        {
            try
            {
                File.SetAttributes(iconPath, FileAttributes.Normal);
                File.Delete(iconPath);
                changed = true;
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                // Leave the file for a later attempt.
            }
        }

        if (iniRemoved)
        {
            var attributes = File.GetAttributes(folder);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(folder, attributes & ~FileAttributes.ReadOnly);
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsOperational(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;
}
