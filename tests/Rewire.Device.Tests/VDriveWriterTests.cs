using System.Text;
using Rewire.Core.Layouts;
using Rewire.Device.Advantage2;
using Rewire.Device.Snapshots;

namespace Rewire.Device.Tests;

public class VDriveWriterTests : IDisposable
{
    private const string Original = "[caps]>[escape]\r\nnot understood\r\n";

    private readonly FakeDrive _drive = new();
    private readonly FakePlatform _platform = new();
    private readonly SnapshotStore _snapshots;
    private readonly VDriveWriter _writer;

    public VDriveWriterTests()
    {
        _drive.Write("active/qwerty.txt", Original);
        _drive.Write("active/state.txt", "macro_speed=3\r\n");
        _snapshots = new SnapshotStore(Path.Combine(_drive.Root, "..", Path.GetFileName(_drive.Root) + "-snapshots"), "1.0.0");
        _writer = new VDriveWriter(_platform, _snapshots);
    }

    [Fact]
    public async Task Overwrites_the_file_cleans_up_and_ejects()
    {
        var steps = new List<ApplyStep>();

        var result = await _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, AddTabToCaps)], "Tab to Caps",
            new SynchronousProgress<ApplyStep>(steps.Add));

        Assert.Equal(Original + "[tab]>[caps]\r\n", _drive.Read("active/qwerty.txt"));
        Assert.Equal(["qwerty.txt"], result.WrittenFiles);
        Assert.Null(result.EjectError);
        Assert.Equal([_drive.Root], _platform.Cleaned);
        Assert.Equal([_drive.Root], _platform.Ejected);
        Assert.Equal(Enum.GetValues<ApplyStep>(), steps);
    }

    [Fact]
    public async Task Snapshots_the_drive_as_it_was_before_writing()
    {
        var result = await _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, AddTabToCaps)], "Tab to Caps");

        Assert.Equal(Encoding.Latin1.GetBytes(Original), _snapshots.ReadFile(result.Snapshot, "qwerty.txt"));
        Assert.Contains("state.txt", result.Snapshot.Files);
    }

    [Fact]
    public async Task Truncates_when_the_new_file_is_shorter()
    {
        await _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, file => file.RemoveAt(1))], "Shorter");

        Assert.Equal("[caps]>[escape]\r\n", _drive.Read("active/qwerty.txt"));
    }

    [Fact]
    public async Task Skips_files_that_have_not_changed()
    {
        var result = await _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, file => file)], "Nothing");

        Assert.Empty(result.WrittenFiles);
    }

    [Fact]
    public async Task Refuses_when_the_drive_changed_since_editing_began()
    {
        var write = Edit("qwerty.txt", Original, AddTabToCaps);
        _drive.Write("active/qwerty.txt", "[caps]>[lctrl]\r\n");

        var refusal = await Assert.ThrowsAsync<ApplyRefusedException>(() => _writer.ApplyAsync(_drive.Open(), [write], "Late"));

        Assert.Contains("has changed on the keyboard", Assert.Single(refusal.Reasons));
        Assert.Equal("[caps]>[lctrl]\r\n", _drive.Read("active/qwerty.txt"));
        Assert.Empty(_snapshots.List());
    }

    [Fact]
    public async Task Refuses_a_file_that_would_not_round_trip()
    {
        var unreadable = new Remap(new LocationToken("caps]"), new ActionToken("escape"));

        var refusal = await Assert.ThrowsAsync<ApplyRefusedException>(() =>
            _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, file => file.Add(unreadable))], "Bad"));

        Assert.Contains("would not read back the same", Assert.Single(refusal.Reasons));
        Assert.Equal(Original, _drive.Read("active/qwerty.txt"));
    }

    [Theory]
    [InlineData("state.txt", "belongs to the keyboard")]
    [InlineData("STATE.TXT", "belongs to the keyboard")]
    [InlineData("do_not_edit.txt", "belongs to the keyboard")]
    [InlineData("version.txt", "belongs to the keyboard")]
    [InlineData("notes.txt", "is not a layout file name")]
    [InlineData("3_qwerty.txt", "does not exist on the keyboard yet")]
    [InlineData("../qwerty.txt", "is not a layout file name")]
    [InlineData("qwerty.bin", "is not a layout file name")]
    public async Task Refuses_files_it_must_not_write(string fileName, string reason)
    {
        var write = new LayoutWrite(fileName, [], LayoutFile.Empty.Add(new Remap(new LocationToken("caps"), new ActionToken("escape"))));

        var refusal = await Assert.ThrowsAsync<ApplyRefusedException>(() => _writer.ApplyAsync(_drive.Open(), [write], "Bad"));

        Assert.Contains(reason, Assert.Single(refusal.Reasons));
        Assert.Equal("macro_speed=3\r\n", _drive.Read("active/state.txt"));
        Assert.False(File.Exists(Path.Combine(_drive.Active, "3_qwerty.txt")));
    }

    [Fact]
    public async Task Lists_every_reason_and_writes_nothing_when_any_file_is_refused()
    {
        _drive.Write("active/dvorak.txt", "");
        var writes = new[]
        {
            Edit("qwerty.txt", Original, AddTabToCaps),
            new LayoutWrite("state.txt", [], LayoutFile.Empty),
            new LayoutWrite("dvorak.txt", "stale"u8.ToArray(), LayoutFile.Empty),
        };

        var refusal = await Assert.ThrowsAsync<ApplyRefusedException>(() => _writer.ApplyAsync(_drive.Open(), writes, "Mixed"));

        Assert.Equal(2, refusal.Reasons.Count);
        Assert.Equal(Original, _drive.Read("active/qwerty.txt"));
    }

    [Fact]
    public async Task Reports_a_failed_eject_without_undoing_the_write()
    {
        var platform = new FakePlatform { EjectFailure = new IOException("Volume busy.") };
        var writer = new VDriveWriter(platform, _snapshots);

        var result = await writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, AddTabToCaps)], "Busy");

        Assert.Equal("Volume busy.", result.EjectError);
        Assert.Equal(Original + "[tab]>[caps]\r\n", _drive.Read("active/qwerty.txt"));
    }

    [Fact]
    public async Task Reports_system_files_it_could_not_remove_and_still_ejects()
    {
        var platform = new FakePlatform { Leftovers = ["/Volumes/ADVANTAGE2/.TemporaryItems"] };
        var writer = new VDriveWriter(platform, _snapshots);

        var result = await writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, AddTabToCaps)], "Locked");

        Assert.Equal(["/Volumes/ADVANTAGE2/.TemporaryItems"], result.LeftoverSystemFiles);
        Assert.Equal(["qwerty.txt"], result.WrittenFiles);
        Assert.Equal([_drive.Root], platform.Ejected);
    }

    [Fact]
    public async Task Restores_a_snapshot_through_the_same_flow()
    {
        var first = await _writer.ApplyAsync(_drive.Open(), [Edit("qwerty.txt", Original, AddTabToCaps)], "Tab to Caps");
        var drive = _drive.Open();

        await _writer.ApplyAsync(drive, _writer.PlanRestore(drive, first.Snapshot), "Undo Tab to Caps");

        Assert.Equal(Original, _drive.Read("active/qwerty.txt"));
        Assert.Equal("macro_speed=3\r\n", _drive.Read("active/state.txt"));
    }

    public void Dispose()
    {
        _drive.Dispose();
        var snapshots = Path.Combine(_drive.Root, "..", Path.GetFileName(_drive.Root) + "-snapshots");
        if (Directory.Exists(snapshots))
            Directory.Delete(snapshots, recursive: true);
    }

    private static LayoutFile AddTabToCaps(LayoutFile file) =>
        file.Add(new Remap(new LocationToken("tab"), new ActionToken("caps")));

    private static LayoutWrite Edit(string fileName, string expected, Func<LayoutFile, LayoutFile> change)
    {
        var bytes = Encoding.Latin1.GetBytes(expected);
        return new LayoutWrite(fileName, bytes, change(LayoutFile.Parse(bytes)));
    }

    // Progress<T> posts to the thread pool, which would race the assertions.
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
