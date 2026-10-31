using System.Text.Json;
using Rewire.Device.Platform;

namespace Rewire.Device.Snapshots;

public sealed record Snapshot(string Id, DateTimeOffset TakenAt, string Note, string AppVersion, IReadOnlyList<string> Files);

/// <summary>
/// Keeps a copy of the keyboard's <c>active</c> folder from before every apply, one folder per snapshot.
/// </summary>
public sealed class SnapshotStore(string directory, string appVersion, TimeProvider? time = null)
{
    private const string ManifestName = "snapshot.json";
    private const string FilesFolder = "active";
    private const string PartialSuffix = ".partial";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Where Rewire keeps snapshots for the current user.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewire", "snapshots");

    public Snapshot Take(string activeDirectory, string note)
    {
        var takenAt = _time.GetUtcNow();
        var id = UniqueId(takenAt);

        // Copied under a temporary name and renamed once complete, so a crash mid-copy
        // never leaves something that looks like a usable snapshot.
        var partial = Path.Combine(directory, id + PartialSuffix);
        var files = new List<string>();
        foreach (var source in Directory.EnumerateFiles(activeDirectory, "*", SystemFiles.IncludeHidden))
        {
            if (SystemFiles.IsSystemFile(Path.GetFileName(source)))
                continue;

            var relative = Path.GetRelativePath(activeDirectory, source);
            var target = Path.Combine(partial, FilesFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        Directory.CreateDirectory(partial);
        var snapshot = new Snapshot(id, takenAt, note, appVersion, files);
        File.WriteAllText(Path.Combine(partial, ManifestName), JsonSerializer.Serialize(snapshot, Json));
        Directory.Move(partial, Path.Combine(directory, id));
        return snapshot;
    }

    /// <summary>Every complete snapshot, newest first.</summary>
    public IReadOnlyList<Snapshot> List()
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateDirectories(directory)
            .Where(path => !path.EndsWith(PartialSuffix, StringComparison.Ordinal))
            .Select(path => Path.Combine(path, ManifestName))
            .Where(File.Exists)
            .Select(manifest => JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(manifest), Json)!)
            .OrderByDescending(snapshot => snapshot.TakenAt)
            .ToList();
    }

    public byte[] ReadFile(Snapshot snapshot, string relativePath) =>
        File.ReadAllBytes(Path.Combine(directory, snapshot.Id, FilesFolder, relativePath));

    private string UniqueId(DateTimeOffset takenAt)
    {
        var stem = takenAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'");
        var id = stem;
        for (var n = 2; Directory.Exists(Path.Combine(directory, id)) || Directory.Exists(Path.Combine(directory, id + PartialSuffix)); n++)
            id = $"{stem}-{n}";
        return id;
    }
}
