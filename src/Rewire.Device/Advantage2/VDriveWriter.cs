using Rewire.Core.Layouts;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;

namespace Rewire.Device.Advantage2;

/// <summary>A new version of one layout file, and the drive's bytes it was edited against.</summary>
public sealed record LayoutWrite(string FileName, byte[] Expected, LayoutFile Updated);

public enum ApplyStep
{
    Validating,
    Snapshotting,
    Writing,
    Cleaning,
    Ejecting,
}

/// <param name="LeftoverSystemFiles">OS files that could not be removed. Harmless to the keyboard, but worth reporting.</param>
/// <param name="EjectError">Why ejecting failed, if it did. The files are written either way.</param>
public sealed record ApplyResult(
    Snapshot Snapshot,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> LeftoverSystemFiles,
    string? EjectError);

/// <summary>Nothing was written: every reason is listed so the user can fix them all at once.</summary>
public sealed class ApplyRefusedException(IReadOnlyList<string> reasons)
    : Exception(string.Join(Environment.NewLine, reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Writing stopped part of the way through; the snapshot holds the drive as it was.</summary>
public sealed class ApplyFailedException(Snapshot snapshot, IReadOnlyList<string> writtenFiles, Exception inner)
    : Exception($"Writing to the keyboard failed after {writtenFiles.Count} file(s). Restore snapshot {snapshot.Id} to undo.", inner)
{
    public Snapshot Snapshot { get; } = snapshot;

    public IReadOnlyList<string> WrittenFiles { get; } = writtenFiles;
}

/// <summary>
/// Writes layout files to the v-drive in the one way the keyboard tolerates: validated,
/// snapshotted first, and overwritten in place.
/// </summary>
public sealed class VDriveWriter(IDrivePlatform platform, SnapshotStore snapshots)
{
    public async Task<ApplyResult> ApplyAsync(
        VDrive drive,
        IReadOnlyList<LayoutWrite> writes,
        string note,
        IProgress<ApplyStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(ApplyStep.Validating);
        var pending = Prepare(drive, writes);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(ApplyStep.Snapshotting);
        var snapshot = snapshots.Take(drive.ActiveDirectory, note);

        // Past this point a cancellation would leave the drive half written, so it is ignored until the eject.
        progress?.Report(ApplyStep.Writing);
        var written = new List<string>();
        try
        {
            foreach (var (fileName, bytes) in pending)
            {
                var path = Path.Combine(drive.ActiveDirectory, fileName);
                OverwriteInPlace(path, bytes);
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    throw new IOException($"{fileName} did not read back as written.");
                written.Add(fileName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ApplyFailedException(snapshot, written, exception);
        }

        progress?.Report(ApplyStep.Cleaning);
        var leftovers = platform.RemoveSystemFiles(drive.Root);

        progress?.Report(ApplyStep.Ejecting);
        string? ejectError = null;
        try
        {
            await platform.EjectAsync(drive.Root, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ejectError = exception.Message;
        }

        return new ApplyResult(snapshot, written, leftovers, ejectError);
    }

    /// <summary>The writes that put a snapshot's layout files back, to pass to <see cref="ApplyAsync"/>.</summary>
    public IReadOnlyList<LayoutWrite> PlanRestore(VDrive drive, Snapshot snapshot) =>
        snapshot.Files
            .Where(VDrive.IsLayoutFile)
            .Select(file =>
            {
                var current = Path.Combine(drive.ActiveDirectory, file);
                return new LayoutWrite(
                    file,
                    File.Exists(current) ? File.ReadAllBytes(current) : [],
                    LayoutFile.Parse(snapshots.ReadFile(snapshot, file)));
            })
            .ToList();

    private static List<(string FileName, byte[] Bytes)> Prepare(VDrive drive, IReadOnlyList<LayoutWrite> writes)
    {
        var reasons = new List<string>();
        var pending = new List<(string, byte[])>();

        foreach (var write in writes)
        {
            var name = write.FileName;
            if (!VDrive.IsLayoutFile(name))
            {
                reasons.Add(VDrive.IsKeyboardFile(name)
                    ? $"{name} belongs to the keyboard and is never written by Rewire."
                    : $"\"{name}\" is not a layout file name.");
                continue;
            }

            var bytes = write.Updated.ToBytes();
            if (!RoundTrips(write.Updated, bytes))
            {
                reasons.Add($"{name} would not read back the same after saving, so it was not written.");
                continue;
            }

            // The keyboard only accepts edits to files it has already conditioned, so
            // creating one is a separate, guided step rather than a side effect of applying.
            var path = Path.Combine(drive.ActiveDirectory, name);
            if (!File.Exists(path))
            {
                reasons.Add($"{name} does not exist on the keyboard yet and has to be created first.");
                continue;
            }

            var current = File.ReadAllBytes(path);
            if (!current.AsSpan().SequenceEqual(write.Expected))
            {
                reasons.Add($"{name} has changed on the keyboard since you started editing it. Reload it and try again.");
                continue;
            }

            if (!current.AsSpan().SequenceEqual(bytes))
                pending.Add((name, bytes));
        }

        if (reasons.Count > 0)
            throw new ApplyRefusedException(reasons);
        return pending;
    }

    private static bool RoundTrips(LayoutFile file, byte[] bytes)
    {
        var reparsed = LayoutFile.Parse(bytes);
        return reparsed.ToBytes().AsSpan().SequenceEqual(bytes) && reparsed.Lines.SequenceEqual(file.Lines);
    }

    // Writing first and truncating after means the file is never briefly empty.
    private static void OverwriteInPlace(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
