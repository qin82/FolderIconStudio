using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace FolderIconStudio.Services;

public sealed class IconEntry
{
    public IconEntry(string name, string filePath, string hash, BitmapSource? thumbnail)
    {
        Name = name;
        FilePath = filePath;
        Hash = hash;
        Thumbnail = thumbnail;
    }

    public string Name { get; }
    public string FilePath { get; }
    public string Hash { get; }
    public BitmapSource? Thumbnail { get; }
    public string FileName => Path.GetFileName(FilePath);
}

public sealed class IconGroup : INotifyPropertyChanged
{
    private string _name;

    public IconGroup(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public ObservableCollection<IconEntry> Icons { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ImportReport(int Added, int Skipped, IReadOnlyList<string> Errors);

public sealed class IconLibraryService
{
    private static readonly string[] SupportedExtensions = [".ico", ".png", ".jpg", ".jpeg", ".bmp"];

    private sealed record StoredIcon(string FileName, string Name);

    private sealed class StoredGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<StoredIcon> Icons { get; set; } = [];
    }

    private sealed class StoredLibrary
    {
        public int Version { get; set; } = 2;
        public string? ActiveGroupId { get; set; }
        public List<StoredGroup> Groups { get; set; } = [];
    }

    private readonly string _indexPath;

    public IconLibraryService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LibraryPath = Path.Combine(localAppData, "FolderIconStudio", "icons");
        Directory.CreateDirectory(LibraryPath);
        _indexPath = Path.Combine(LibraryPath, "library.json");
        Load();
    }

    public string LibraryPath { get; }
    public ObservableCollection<IconGroup> Groups { get; } = [];
    public IconGroup Current { get; private set; } = null!;
    public ObservableCollection<IconEntry> Icons => Current.Icons;
    public int TotalCount => Groups.Sum(group => group.Icons.Count);
    public event Action? Changed;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IconGroup AddGroup(string name)
    {
        var group = new IconGroup(Guid.NewGuid().ToString("N"), name);
        Groups.Add(group);
        Current = group;
        SaveIndex();
        return group;
    }

    public static string? NormalizeGroupName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 30 ? trimmed[..30] : trimmed;
    }

    public bool IsNameAvailable(string name, IconGroup? except = null) =>
        !Groups.Any(group => !ReferenceEquals(group, except)
            && group.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool RenameGroup(IconGroup group, string name)
    {
        var trimmed = NormalizeGroupName(name);
        if (trimmed is null || !IsNameAvailable(trimmed, group)) return false;

        group.Name = trimmed;
        SaveIndex();
        return true;
    }

    public bool RemoveGroup(IconGroup group)
    {
        if (Groups.Count <= 1) return false;
        if (!Groups.Remove(group)) return false;

        foreach (var entry in group.Icons)
        {
            try
            {
                if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (ReferenceEquals(Current, group)) Current = Groups[0];
        SaveIndex();
        return true;
    }

    public void SwitchTo(IconGroup group)
    {
        if (!Groups.Contains(group)) return;
        Current = group;
        SaveIndex();
    }

    public ImportReport Import(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        var errors = new List<string>();
        var knownHashes = new HashSet<string>(Current.Icons.Select(icon => icon.Hash), StringComparer.OrdinalIgnoreCase);

        foreach (var source in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source) || !IsSupported(source)) continue;

            var temp = Path.Combine(LibraryPath, $".import-{Guid.NewGuid():N}.tmp");
            try
            {
                if (Path.GetExtension(source).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, temp, overwrite: true);
                }
                else
                {
                    IcoEncoder.ConvertImageToIco(source, temp);
                }

                var thumbnail = LoadThumbnail(temp);
                if (thumbnail is null) throw new InvalidDataException("无法识别为图标文件。");

                var hash = HashFile(temp);
                if (!knownHashes.Add(hash))
                {
                    skipped++;
                    continue;
                }

                var target = Path.Combine(LibraryPath, $"{Guid.NewGuid():N}.ico");
                File.Move(temp, target);

                var entry = new IconEntry(MakeUniqueName(Path.GetFileNameWithoutExtension(source)), target, hash, thumbnail);
                Current.Icons.Add(entry);
                added++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or InvalidOperationException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(source)}：{exception.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }

        if (added > 0) SaveIndex();
        return new ImportReport(added, skipped, errors);
    }

    public void Remove(IconEntry entry)
    {
        if (!Current.Icons.Remove(entry)) return;
        try
        {
            if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        SaveIndex();
    }

    public static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(Math.Max(candidate.PixelWidth, candidate.PixelHeight) - 64))
                .First();
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or ArgumentException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    private void Load()
    {
        var needsSave = false;

        try
        {
            var text = File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : string.Empty;
            if (text.TrimStart().StartsWith('['))
            {
                // Legacy single-library index: move every icon into the first group.
                var legacy = JsonSerializer.Deserialize<List<StoredIcon>>(text) ?? [];
                var migrated = new StoredGroup
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "图标库 1",
                    Icons = legacy
                };
                AddStoredGroup(migrated);
                needsSave = true;
            }
            else if (text.Length > 0)
            {
                var stored = JsonSerializer.Deserialize<StoredLibrary>(text) ?? new StoredLibrary();
                foreach (var group in stored.Groups) AddStoredGroup(group);

                var active = Groups.FirstOrDefault(group =>
                    string.Equals(group.Id, stored.ActiveGroupId, StringComparison.OrdinalIgnoreCase));
                if (active is not null) Current = active;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Groups.Clear();
        }

        if (Groups.Count == 0)
        {
            var group = new IconGroup(Guid.NewGuid().ToString("N"), "图标库 1");
            Groups.Add(group);
            Current = group;
            needsSave = true;
        }
        else if (Current is null)
        {
            Current = Groups[0];
        }

        if (needsSave) SaveIndex();
    }

    private void AddStoredGroup(StoredGroup stored)
    {
        var name = string.IsNullOrWhiteSpace(stored.Name) ? "图标库" : stored.Name.Trim();
        var group = new IconGroup(string.IsNullOrWhiteSpace(stored.Id) ? Guid.NewGuid().ToString("N") : stored.Id, name);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in stored.Icons)
        {
            var path = Path.Combine(LibraryPath, item.FileName);
            if (!File.Exists(path)) continue;

            var thumbnail = LoadThumbnail(path);
            if (thumbnail is null) continue;

            var hash = HashFile(path);
            if (!hashes.Add(hash)) continue;

            group.Icons.Add(new IconEntry(item.Name, path, hash, thumbnail));
        }

        Groups.Add(group);
    }

    private void SaveIndex()
    {
        var stored = new StoredLibrary
        {
            ActiveGroupId = Current?.Id,
            Groups = Groups.Select(group => new StoredGroup
            {
                Id = group.Id,
                Name = group.Name,
                Icons = group.Icons.Select(entry => new StoredIcon(entry.FileName, entry.Name)).ToList()
            }).ToList()
        };

        try
        {
            var temp = _indexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _indexPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        Changed?.Invoke();
    }

    private string MakeUniqueName(string sourceName)
    {
        var name = (sourceName ?? string.Empty).Trim();
        if (name.Length == 0) name = "图标";
        if (name.Length > 40) name = name[..40];

        var used = new HashSet<string>(Current.Icons.Select(icon => icon.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
