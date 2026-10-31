namespace Rewire.Device.Platform;

/// <summary>Files and folders that macOS writes to any FAT drive it mounts.</summary>
internal static class SystemFiles
{
    // .TemporaryItems is left alone: macOS keeps it locked while the drive is mounted.
    public static readonly string[] RootDirectories = [".fseventsd", ".Spotlight-V100"];

    public static bool IsSystemFile(string fileName) =>
        fileName.StartsWith("._", StringComparison.Ordinal) || fileName == ".DS_Store";

    // .NET treats every dot-file on Unix as hidden, and skips hidden entries by default.
    public static readonly EnumerationOptions IncludeHidden = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
    };

    /// <returns>The paths that could not be removed.</returns>
    public static IReadOnlyList<string> RemoveFrom(string root)
    {
        var leftovers = new List<string>();

        foreach (var name in RootDirectories)
        {
            var directory = Path.Combine(root, name);
            if (Directory.Exists(directory))
                TryRemove(directory, () => Directory.Delete(directory, recursive: true), leftovers);
        }

        var litter = Directory.EnumerateFiles(root, "*", IncludeHidden)
            .Where(path => IsSystemFile(Path.GetFileName(path)))
            .ToList();
        foreach (var path in litter)
            TryRemove(path, () => File.Delete(path), leftovers);

        return leftovers;
    }

    // macOS protects some of its own files, and none of them affect the keyboard,
    // so one that cannot be removed is reported rather than stopping the rest.
    private static void TryRemove(string path, Action remove, List<string> leftovers)
    {
        try
        {
            remove();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            leftovers.Add(path);
        }
    }
}
