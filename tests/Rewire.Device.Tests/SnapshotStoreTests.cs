using Rewire.Device.Snapshots;

namespace Rewire.Device.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly FakeDrive _drive = new();
    private readonly string _store = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Copies_every_active_file_except_system_files()
    {
        _drive.Write("active/qwerty.txt", "[caps]>[escape]\r\n");
        _drive.Write("active/state.txt", "macro_speed=3\r\n");
        _drive.Write("active/._qwerty.txt", "resource fork");
        var store = new SnapshotStore(_store, "1.0.0");

        var snapshot = store.Take(_drive.Active, "Before Esc on Caps");

        Assert.Equal(["qwerty.txt", "state.txt"], snapshot.Files.Order());
        Assert.Equal("[caps]>[escape]\r\n"u8.ToArray(), store.ReadFile(snapshot, "qwerty.txt"));
    }

    [Fact]
    public void Lists_snapshots_newest_first_with_their_details()
    {
        _drive.Write("active/qwerty.txt", "");
        var clock = new SteppingClock();
        var store = new SnapshotStore(_store, "1.0.0", clock);

        store.Take(_drive.Active, "First");
        store.Take(_drive.Active, "Second");

        var listed = store.List();
        Assert.Equal(["Second", "First"], listed.Select(snapshot => snapshot.Note));
        Assert.Equal("1.0.0", listed[0].AppVersion);
        Assert.Equal(["qwerty.txt"], listed[0].Files);
    }

    [Fact]
    public void Two_snapshots_in_the_same_millisecond_get_different_ids()
    {
        _drive.Write("active/qwerty.txt", "");
        var store = new SnapshotStore(_store, "1.0.0", new FixedClock());

        var first = store.Take(_drive.Active, "First");
        var second = store.Take(_drive.Active, "Second");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Ignores_an_unfinished_snapshot()
    {
        Directory.CreateDirectory(Path.Combine(_store, "20261030T120000000Z.partial", "active"));

        Assert.Empty(new SnapshotStore(_store, "1.0.0").List());
    }

    public void Dispose()
    {
        _drive.Dispose();
        if (Directory.Exists(_store))
            Directory.Delete(_store, recursive: true);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);
    }
}
