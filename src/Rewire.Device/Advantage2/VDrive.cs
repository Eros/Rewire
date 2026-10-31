using System.Text.RegularExpressions;
using Rewire.Device.Platform;

namespace Rewire.Device.Advantage2;

/// <summary>The Advantage2's v-drive, opened by the user with Program+F1 and mounted by the OS.</summary>
public sealed partial class VDrive
{
    public const string StateFileName = "state.txt";

    // Written by the keyboard itself: do_not_edit.txt holds the compiled macro playback
    // and version.txt the firmware version.
    private static readonly string[] KeyboardFileNames = [StateFileName, "do_not_edit.txt", "version.txt"];

    private VDrive(string root, string? label)
    {
        Root = root;
        Label = label;
    }

    public string Root { get; }

    public string? Label { get; }

    public string ActiveDirectory => Path.Combine(Root, "active");

    /// <remarks>
    /// The volume label may differ between firmware versions, so the drive is recognised
    /// by its folder structure alone.
    /// </remarks>
    public static VDrive? TryOpen(MountedVolume volume) =>
        Directory.Exists(Path.Combine(volume.Root, "active")) && Directory.Exists(Path.Combine(volume.Root, "firmware"))
            ? new VDrive(volume.Root, volume.Label)
            : null;

    public IReadOnlyList<string> LayoutFileNames() =>
        Directory.EnumerateFiles(ActiveDirectory, "*.txt")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsLayoutFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public byte[] ReadFile(string fileName) => File.ReadAllBytes(Path.Combine(ActiveDirectory, fileName));

    public StateFile ReadState() => StateFile.Parse(ReadFile(StateFileName));

    /// <summary>The base <c>qwerty.txt</c> and <c>dvorak.txt</c>, or a Program+X profile such as <c>2_qwerty.txt</c>.</summary>
    public static bool IsLayoutFile(string fileName) => LayoutFileName().IsMatch(fileName);

    internal static bool IsKeyboardFile(string fileName) =>
        KeyboardFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^([0-9a-z]_)?(qwerty|dvorak)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex LayoutFileName();
}
