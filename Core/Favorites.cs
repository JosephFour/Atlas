using System.Text.Json;

namespace Atlas.Core;

/// <summary>Starred clips, kept as a set of paths next to the thumbnail cache.</summary>
public static class Favorites
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "favorites.json");
    static readonly object Gate = new();
    static HashSet<string>? _set;

    static HashSet<string> Set
    {
        get
        {
            if (_set != null) return _set;
            try
            {
                _set = File.Exists(FilePath)
                    ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new(), StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch { _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            return _set;
        }
    }

    public static bool Contains(string path) { lock (Gate) return Set.Contains(path); }

    public static bool Toggle(string path)
    {
        lock (Gate)
        {
            bool now = Set.Add(path) || !Set.Remove(path);
            Save();
            return now;
        }
    }

    public static void Rename(string from, string to)
    {
        lock (Gate) { if (Set.Remove(from)) { Set.Add(to); Save(); } }
    }

    public static void Remove(string path)
    {
        lock (Gate) { if (Set.Remove(path)) Save(); }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Set.ToList()));
        }
        catch (Exception ex) { Log.Warn($"Favorites not saved: {ex.Message}"); }
    }
}
