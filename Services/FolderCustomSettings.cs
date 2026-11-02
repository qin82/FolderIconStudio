using System.Runtime.InteropServices;

namespace FolderIconStudio.Services;

/// <summary>
/// Explorer only re-reads a folder's desktop.ini through this shell API; plain
/// SHChangeNotify calls leave the cached folder icon untouched on current Windows builds.
/// The icon reference stays relative so the folder remains portable.
/// </summary>
internal static class FolderCustomSettings
{
    private const uint FcsmIconFile = 0x00000010;
    private const uint FcsForceWrite = 0x00000002;

    public static bool TrySetIcon(string folder, string iconFileName)
    {
        try
        {
            var settings = Create(FcsmIconFile);
            settings.pszIconFile = iconFileName;
            settings.cchIconFile = 0;
            settings.iIconIndex = 0;
            return SHGetSetFolderCustomSettings(ref settings, folder, FcsForceWrite) == 0;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return false;
        }
    }

    public static bool TryClearIcon(string folder)
    {
        try
        {
            var settings = Create(FcsmIconFile);
            settings.pszIconFile = null;
            settings.cchIconFile = 0;
            return SHGetSetFolderCustomSettings(ref settings, folder, FcsForceWrite) == 0;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return false;
        }
    }

    private static ShFolderCustomSettings Create(uint mask) => new()
    {
        dwSize = (uint)Marshal.SizeOf<ShFolderCustomSettings>(),
        dwMask = mask
    };

    private static bool IsUnavailable(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException
            or ArgumentException;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFolderCustomSettings
    {
        public uint dwSize;
        public uint dwMask;
        public IntPtr pvid;
        public string? pszWebViewTemplate;
        public uint cchWebViewTemplate;
        public string? pszWebViewTemplateVersion;
        public string? pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        public string? pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        public string? pszLogo;
        public uint cchLogo;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetSetFolderCustomSettings(ref ShFolderCustomSettings settings, string folder, uint readWrite);
}
