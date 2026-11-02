using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FolderIconStudio.Services;

public static class ShellRefresh
{
    private const uint ShcneAttributes = 0x00000800;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;

    private static readonly object CacheRebuildLock = new();

    /// <summary>
    /// Announces the folder and its parent so open views re-stat the folder. The icon
    /// reference itself is refreshed by <see cref="FolderCustomSettings"/>; these
    /// notifications cover attributes, sorting and icon-cache bookkeeping.
    /// </summary>
    public static void RefreshFolders(IEnumerable<string> folders)
    {
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var any = false;

        foreach (var folder in folders)
        {
            any = true;
            try
            {
                var attributes = File.GetAttributes(folder);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(folder, attributes & ~FileAttributes.ReadOnly);
                    File.SetAttributes(folder, attributes);
                }

                SHChangeNotify(ShcneAttributes, ShcnfPathW | ShcnfFlush, folder, IntPtr.Zero);
                SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlush, folder, IntPtr.Zero);

                var parent = Path.GetDirectoryName(folder);
                if (parent is not null && parents.Add(parent))
                {
                    SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlush, parent, IntPtr.Zero);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // Refresh is best effort; the icon is already on disk and shows after the next view refresh.
            }
        }

        if (any)
        {
            RefreshIcons();
            RebuildIconCache();
        }
    }

    public static void RefreshIcons()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Explorer refresh is best effort; the icon still applies on the next refresh.
        }
    }

    /// <summary>Makes Explorer re-read context menus after the registry entries were rebuilt.</summary>
    public static void RefreshMenus()
    {
        RefreshIcons();
        RunIe4uinit("-show");
    }

    /// <summary>
    /// Current Windows builds keep serving the old bitmap from the per-user icon cache even
    /// after the desktop.ini reference changes, so a stuck view only repaints once the cache
    /// is rebuilt. Runs inline: apply/clear/repair already execute on a worker thread, and a
    /// background task would be cut short when QuickApply exits right after applying.
    /// </summary>
    private static void RebuildIconCache()
    {
        lock (CacheRebuildLock)
        {
            RunIe4uinit("-ClearIconCache");
            Thread.Sleep(250);
            RunIe4uinit("-show");
        }
    }

    private static void RunIe4uinit(string arguments)
    {
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null) return;
            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already exited.
                }
            }
        }
        catch (Exception)
        {
            // Missing or blocked ie4uinit only costs the forced repaint, not the icon itself.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
