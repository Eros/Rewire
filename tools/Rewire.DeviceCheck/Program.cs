using Rewire.Core.Layouts;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;

const string Usage = """
    Exercises Rewire's device service against a real Advantage2.

    Usage: rewire-device-check <command>

      detect                 Wait for the v-drive and describe it.
      check                  Confirm every layout file round-trips byte for byte. Read-only.
      snapshot [note]        Copy the active folder to the snapshot store.
      snapshots              List snapshots, newest first.
      add <file> <line>      Append a line, e.g. add qwerty.txt "[caps]>[escape]", then eject.
      restore <snapshot-id>  Put a snapshot's layout files back, then eject.
      eject                  Eject the v-drive.
      watch                  Print each time the v-drive or a disk appears or disappears, to see
                             what each Program+F1 press does. Read-only.

      selftest [file] [line] Run all of the above as one guided test: check, snapshot, add a
                             remap, confirm the keyboard uses it, restore, confirm it's gone.
                             Defaults to qwerty.txt and the first of F12, F11, F10 or F9
                             that it doesn't already remap, sending z.

    Open the v-drive first: Program+Shift+Esc for Power User Mode, then Program+F1.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var snapshotDirectory = SnapshotStore.DefaultDirectory;
var platform = DrivePlatform.ForCurrentOs();
var snapshots = new SnapshotStore(snapshotDirectory, "device-check");
var writer = new VDriveWriter(platform, snapshots);

// The first Ctrl+C stops the current step; the self-test still restores the keyboard
// afterwards, and only a second Ctrl+C abandons that too.
using var cancel = new CancellationTokenSource();
using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (cancel.IsCancellationRequested)
    {
        quit.Cancel();
        return;
    }

    cancel.Cancel();
    if (args is ["selftest", ..])
        Console.Error.WriteLine(" Stopping. If anything was written, the keyboard is restored next; press Ctrl+C again to skip that.");
};

try
{
    return args switch
    {
        ["detect"] => await Detect(),
        ["check"] => await Check(),
        ["snapshot", .. var note] => await Snapshot(string.Join(' ', note)),
        ["snapshots"] => ListSnapshots(),
        ["add", var file, var line] => await Add(file, line),
        ["restore", var id] => await Restore(id),
        ["eject"] => await Eject(),
        ["watch"] => await Watch(),
        ["selftest"] => await SelfTest("qwerty.txt", null),
        ["selftest", var file] => await SelfTest(file, null),
        ["selftest", var file, var line] => await SelfTest(file, line),
        _ => Fail($"Unknown command.{Environment.NewLine}{Environment.NewLine}{Usage}"),
    };
}
catch (OperationCanceledException)
{
    return Fail("Cancelled.");
}
catch (ApplyRefusedException refused)
{
    Console.Error.WriteLine("Nothing was written:");
    foreach (var reason in refused.Reasons)
        Console.Error.WriteLine($"  {reason}");
    return 1;
}
catch (ApplyFailedException failed)
{
    Console.Error.WriteLine(failed.Message);
    Console.Error.WriteLine($"  Written before the failure: {string.Join(", ", failed.WrittenFiles)}");
    Console.Error.WriteLine($"  Cause: {failed.InnerException?.Message}");
    return 1;
}
catch (IOException exception)
{
    // Reached only before any write starts; failures during a write arrive as ApplyFailedException.
    return Fail($"The v-drive could not be read, so nothing was written. Was it closed part way through? ({exception.Message})");
}

Task<VDrive> WaitForDrive() => Prompts.WaitForDriveAsync(platform, cancel.Token);

async Task<int> Detect()
{
    var drive = await WaitForDrive();
    Console.WriteLine($"Found the v-drive at {drive.Root} (label: {drive.Label ?? "none"}).");

    Console.WriteLine("Layout files:");
    foreach (var name in drive.LayoutFileNames())
        Console.WriteLine($"  {name} ({drive.ReadFile(name).Length} bytes)");

    if (File.Exists(Path.Combine(drive.ActiveDirectory, VDrive.StateFileName)))
    {
        Console.WriteLine("state.txt:");
        foreach (var (key, value) in drive.ReadState().Settings)
            Console.WriteLine($"  {key} = {value}");
    }
    else
    {
        Console.WriteLine("There is no state.txt.");
    }

    Console.WriteLine("Every entry in the active folder:");
    foreach (var path in Directory.EnumerateFileSystemEntries(drive.ActiveDirectory, "*",
                 new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None }))
        Console.WriteLine($"  {Path.GetRelativePath(drive.ActiveDirectory, path)}");

    return 0;
}

async Task<int> Check()
{
    var drive = await WaitForDrive();
    var allPassed = true;

    foreach (var name in drive.LayoutFileNames())
    {
        var bytes = drive.ReadFile(name);
        var file = LayoutFile.Parse(bytes);
        var roundTrips = file.ToBytes().AsSpan().SequenceEqual(bytes);
        allPassed &= roundTrips;

        var remaps = file.Lines.OfType<Remap>().Count();
        var macros = file.Lines.OfType<Macro>().Count();
        var unknown = file.Lines.OfType<UnknownLine>().Where(line => line.RawText.Trim().Length > 0).ToList();
        Console.WriteLine($"{(roundTrips ? "ok  " : "FAIL")} {name}: {remaps} remaps, {macros} macros, {unknown.Count} unrecognised lines");
        foreach (var line in unknown)
            Console.WriteLine($"       {Prompts.Visible(line.RawText)}");
    }

    return allPassed ? 0 : 1;
}

async Task<int> Snapshot(string note)
{
    var drive = await WaitForDrive();
    var snapshot = snapshots.Take(drive.ActiveDirectory, note.Length > 0 ? note : "Taken by device check");
    Console.WriteLine($"Took snapshot {snapshot.Id} of {snapshot.Files.Count} files in {snapshotDirectory}.");
    return 0;
}

int ListSnapshots()
{
    var all = snapshots.List();
    if (all.Count == 0)
        Console.WriteLine($"There are no snapshots in {snapshotDirectory}.");
    foreach (var snapshot in all)
        Console.WriteLine($"{snapshot.Id}  {snapshot.TakenAt.ToLocalTime():g}  {snapshot.Note} ({snapshot.Files.Count} files)");
    return 0;
}

async Task<int> Add(string fileName, string lineText)
{
    var line = LayoutLineParser.Parse(lineText);
    if (line is UnknownLine)
        return Fail($"\"{lineText}\" is not a remap or macro.");

    var drive = await WaitForDrive();
    if (!drive.LayoutFileNames().Contains(fileName, StringComparer.OrdinalIgnoreCase))
        return Fail($"{fileName} is not on the keyboard. It has: {string.Join(", ", drive.LayoutFileNames())}.");

    var current = drive.ReadFile(fileName);
    var write = new LayoutWrite(fileName, current, LayoutFile.Parse(current).Add(line));

    if (!Prompts.Confirm($"Append {line.Render()} to {fileName} on the keyboard?"))
        return Fail("Nothing was written.");

    return Report(await writer.ApplyAsync(drive, [write], $"Device check: add {line.Render()} to {fileName}", Steps(), cancel.Token));
}

async Task<int> Restore(string id)
{
    var snapshot = snapshots.List().FirstOrDefault(snapshot => snapshot.Id == id);
    if (snapshot is null)
        return Fail($"There is no snapshot {id}. Run \"snapshots\" to list them.");

    var drive = await WaitForDrive();
    var writes = writer.PlanRestore(drive, snapshot);

    if (!Prompts.Confirm($"Restore {string.Join(", ", writes.Select(write => write.FileName))} from {snapshot.Id} ({snapshot.Note})?"))
        return Fail("Nothing was written.");

    return Report(await writer.ApplyAsync(drive, writes, $"Device check: restore {snapshot.Id}", Steps(), cancel.Token));
}

async Task<int> Eject()
{
    var drive = await WaitForDrive();
    await platform.EjectAsync(drive.Root, cancel.Token);
    Console.WriteLine("Ejected. The keyboard offers the v-drive again in a few seconds; press Program+F1 once it has, to close it.");
    return 0;
}

Task<int> SelfTest(string fileName, string? line) =>
    new DeviceSelfTest(platform, writer, snapshots, cancel.Token, quit.Token).RunAsync(fileName, line);

async Task<int> Watch()
{
    Console.WriteLine("Watching for the v-drive. Press Program+F1 and note what each press does. Ctrl+C to stop.");
    var detector = new VDriveDetector(platform);
    string? driveRoot = null;
    var disks = new HashSet<string>();
    var first = true;

    try
    {
        while (true)
        {
            var now = DateTime.Now.ToString("HH:mm:ss.fff");

            // A disk node with no mounted volume means the keyboard offered the drive
            // but macOS did not mount it.
            if (OperatingSystem.IsMacOS())
            {
                var current = WholeDisks();
                foreach (var disk in current.Except(disks).Order())
                    Console.WriteLine($"{now}  disk {disk} {(first ? "is present" : "appeared")}");
                foreach (var disk in disks.Except(current).Order())
                    Console.WriteLine($"{now}  disk {disk} disappeared");
                disks = current;
            }

            var root = detector.Find()?.Root;
            if (root != driveRoot)
            {
                Console.WriteLine(root is null
                    ? $"{now}  v-drive unmounted"
                    : $"{now}  v-drive {(first ? "is mounted" : "mounted")} at {root}");
                driveRoot = root;
            }
            else if (first && root is null)
            {
                Console.WriteLine($"{now}  v-drive is not mounted");
            }

            first = false;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancel.Token);
        }
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
}

static HashSet<string> WholeDisks() =>
    Directory.EnumerateFiles("/dev", "disk*")
        .Select(Path.GetFileName)
        .OfType<string>()
        .Where(name => name.Length > 4 && name[4..].All(char.IsAsciiDigit))
        .ToHashSet();

int Report(ApplyResult result)
{
    Console.WriteLine(result.WrittenFiles.Count == 0
        ? "Nothing needed writing; every file already matched."
        : $"Wrote {string.Join(", ", result.WrittenFiles)}.");
    Console.WriteLine($"Snapshot of the drive before writing: {result.Snapshot.Id}");
    if (result.LeftoverSystemFiles.Count > 0)
        Console.WriteLine($"Could not remove these macOS system files: {string.Join(", ", result.LeftoverSystemFiles)}");

    if (result.EjectError is not null)
        return Fail($"The files are written, but ejecting failed: {result.EjectError} Eject the drive yourself before pressing Program+F1.");

    Console.WriteLine("Ejected. The keyboard offers the v-drive again in a few seconds; press Program+F1 once it has, to close it and load the change.");
    return 0;
}

static IProgress<ApplyStep> Steps() => new ConsoleProgress();

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

internal sealed class ConsoleProgress : IProgress<ApplyStep>
{
    public void Report(ApplyStep step) => Console.WriteLine($"  {step}...");
}
