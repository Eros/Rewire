using Rewire.Device.Advantage2;
using Rewire.Device.Platform;

namespace Rewire.Device.Tests;

public class VDriveTests
{
    [Fact]
    public void Recognises_a_drive_by_its_folders_whatever_its_label()
    {
        using var drive = new FakeDrive();

        var found = new VDriveDetector(new FakePlatform(new MountedVolume(drive.Root, "SOMETHING ELSE"))).Find();

        Assert.Equal(drive.Root, found?.Root);
    }

    [Fact]
    public void Ignores_a_volume_without_a_firmware_folder()
    {
        using var drive = new FakeDrive();
        Directory.Delete(Path.Combine(drive.Root, "firmware"));

        Assert.Null(new VDriveDetector(new FakePlatform(drive.Volume)).Find());
    }

    [Fact]
    public async Task Waits_until_the_drive_is_mounted()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var drive = new FakeDrive();

        var found = await new VDriveDetector(new FakePlatform(drive.Volume)).WaitForDriveAsync(cancel.Token);

        Assert.Equal(drive.Root, found.Root);
    }

    [Fact]
    public void Lists_only_layout_files()
    {
        using var drive = new FakeDrive();
        drive.Write("active/qwerty.txt", "");
        drive.Write("active/dvorak.txt", "");
        drive.Write("active/2_qwerty.txt", "");
        drive.Write("active/A_dvorak.txt", "");
        drive.Write("active/state.txt", "");
        drive.Write("active/do_not_edit.txt", "");
        drive.Write("active/version.txt", "");
        drive.Write("active/._qwerty.txt", "");
        drive.Write("active/notes.txt", "");

        Assert.Equal(["2_qwerty.txt", "A_dvorak.txt", "dvorak.txt", "qwerty.txt"], drive.Open().LayoutFileNames());
    }

    [Fact]
    public void Reads_state_settings()
    {
        using var drive = new FakeDrive();
        drive.Write("active/state.txt", "* comment\r\nstartup_file = qwerty.txt\r\nmacro_speed=3\r\n");

        var state = drive.Open().ReadState();

        Assert.Equal("qwerty.txt", state["startup_file"]);
        Assert.Equal("3", state["MACRO_SPEED"]);
        Assert.Equal(2, state.Settings.Count);
    }
}

public class VDriveDetectorWaitTests
{
    [Fact]
    public async Task Gives_up_waiting_for_the_drive_to_return_after_the_timeout()
    {
        var detector = new VDriveDetector(new FakePlatform());

        Assert.Null(await detector.WaitForReturnAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task Notices_the_drive_returning_after_an_eject()
    {
        using var drive = new FakeDrive();
        var platform = new FakePlatform();
        var detector = new VDriveDetector(platform);

        var returning = detector.WaitForReturnAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        platform.Volumes = [drive.Volume];

        Assert.Equal(drive.Root, (await returning)?.Root);
    }

    [Fact]
    public async Task Cancelling_the_wait_for_return_is_not_mistaken_for_a_timeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var detector = new VDriveDetector(new FakePlatform());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.WaitForReturnAsync(TimeSpan.FromSeconds(10), cancel.Token));
    }

    [Fact]
    public async Task Waits_until_the_drive_closes()
    {
        using var drive = new FakeDrive();
        var platform = new FakePlatform(drive.Volume);

        var closing = new VDriveDetector(platform).WaitUntilClosedAsync(CancellationToken.None);
        Assert.False(closing.IsCompleted);
        platform.Volumes = [];

        await closing.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
