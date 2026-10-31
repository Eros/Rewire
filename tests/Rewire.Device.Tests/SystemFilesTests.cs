using Rewire.Device.Platform;

namespace Rewire.Device.Tests;

public class SystemFilesTests
{
    [Fact]
    public void Removes_what_macOS_leaves_on_the_drive_and_nothing_else()
    {
        using var drive = new FakeDrive();
        drive.Write("active/qwerty.txt", "[caps]>[escape]");
        drive.Write("active/._qwerty.txt", "");
        drive.Write(".DS_Store", "");
        drive.Write("firmware/._2.0.0.upd", "");
        Directory.CreateDirectory(Path.Combine(drive.Root, ".fseventsd", "logs"));
        Directory.CreateDirectory(Path.Combine(drive.Root, ".Spotlight-V100"));
        drive.Write("._Logs", "");

        var leftovers = SystemFiles.RemoveFrom(drive.Root);

        var remaining = Directory.EnumerateFileSystemEntries(drive.Root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.None,
            })
            .Select(path => Path.GetRelativePath(drive.Root, path).Replace('\\', '/'))
            .Order();
        Assert.Empty(leftovers);
        Assert.Equal(["active", "active/qwerty.txt", "firmware"], remaining);
    }
}
