using System.Text;
using Rewire.Core.Layouts;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;

/// <summary>
/// Runs the whole device service against a real keyboard: read, snapshot, write, have the
/// keyboard load the change, restore, and confirm the keyboard is back as it was.
/// </summary>
internal sealed class DeviceSelfTest(
    IDrivePlatform platform,
    VDriveWriter writer,
    SnapshotStore snapshots,
    CancellationToken cancellationToken,
    CancellationToken restoreCancellationToken)
{
    private readonly List<(string Step, bool Passed, string Detail)> _results = [];

    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(15);

    // The keyboard reloads its layout as the drive closes; a short pause avoids testing the key too early.
    private static readonly TimeSpan LoadDelay = TimeSpan.FromSeconds(2);

    // Function keys are rarely remapped, and their token names are the least likely to be wrong.
    private static readonly string[] CandidateKeys = ["f12", "f11", "f10", "f9"];

    /// <param name="lineText">The remap to test with, or null to pick an unused function key.</param>
    public async Task<int> RunAsync(string fileName, string? lineText)
    {
        Remap? requested = null;
        if (lineText is not null)
        {
            requested = LayoutLineParser.Parse(lineText) as Remap;
            if (requested is null)
            {
                Console.Error.WriteLine($"\"{lineText}\" is not a remap. Use one such as [f12]>[z].");
                return 1;
            }
        }

        Console.WriteLine($"""
            This test writes to the keyboard twice: it adds {requested?.Render() ?? "a test remap such as [f12]>[z]"} to {fileName},
            then restores the file from a snapshot. You will press Program+F1 a few times and try the remapped key.
            If anything goes wrong after the first write, it still tries to restore.

            """);
        if (!Prompts.Confirm("Start the self-test?"))
        {
            Console.WriteLine("Nothing was written.");
            return 1;
        }

        Remap? remap = null;
        Snapshot? before = null;
        byte[]? original = null;
        try
        {
            var drive = await Prompts.WaitForDriveAsync(platform, cancellationToken);
            Record("Find the v-drive", true, $"{drive.Root}, label \"{drive.Label}\"");

            if (!CheckRoundTrips(drive))
                return Summarise();

            remap = ChooseTestRemap(drive, fileName, requested);
            if (remap is null)
                return Summarise();

            original = drive.ReadFile(fileName);
            (before, var written) = await Write(drive, fileName, original, remap);
            if (written)
                await CheckKeyboardUsesChange(fileName, original, remap);
        }
        catch (OperationCanceledException)
        {
            Record("Finish the test", false, "Cancelled.");
        }

        if (remap is not null && before is not null && original is not null)
            await RestoreAndCheck(fileName, original, before, remap);

        return Summarise();
    }

    private bool CheckRoundTrips(VDrive drive)
    {
        var failures = drive.LayoutFileNames()
            .Where(name =>
            {
                var bytes = drive.ReadFile(name);
                return !LayoutFile.Parse(bytes).ToBytes().AsSpan().SequenceEqual(bytes);
            })
            .ToList();

        return Record(
            "Read every layout file back byte for byte",
            failures.Count == 0,
            failures.Count == 0 ? $"{drive.LayoutFileNames().Count} files" : $"Differs: {string.Join(", ", failures)}");
    }

    private Remap? ChooseTestRemap(VDrive drive, string fileName, Remap? requested)
    {
        const string step = "Choose the test remap";
        if (!drive.LayoutFileNames().Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            Record(step, false, $"{fileName} is not on the keyboard.");
            return null;
        }

        var remapped = LayoutFile.Parse(drive.ReadFile(fileName)).Lines.OfType<Remap>()
            .ToDictionary(line => line.Location, line => line);

        if (requested is not null)
        {
            if (remapped.TryGetValue(requested.Location, out var existing))
            {
                Record(step, false, $"{fileName} already remaps {requested.Location} ({existing.Render()}). Pick another key.");
                return null;
            }

            Record(step, true, $"{requested.Render()} in {fileName}");
            return requested;
        }

        var free = CandidateKeys.FirstOrDefault(key => !remapped.ContainsKey(new LocationToken(key)));
        if (free is null)
        {
            Record(step, false, $"{fileName} remaps all of {string.Join(", ", CandidateKeys)}. Name a remap to test with.");
            return null;
        }

        var remap = new Remap(new LocationToken(free), new ActionToken("z"));
        Record(step, true, $"{remap.Render()} in {fileName}");
        return remap;
    }

    private async Task<(Snapshot? Before, bool Written)> Write(VDrive drive, string fileName, byte[] original, Remap remap)
    {
        var write = new LayoutWrite(fileName, original, LayoutFile.Parse(original).Add(remap));
        try
        {
            var result = await writer.ApplyAsync(drive, [write], $"Self-test: add {remap.Render()}", new ConsoleProgress(), cancellationToken);
            Record("Snapshot before writing", snapshots.ReadFile(result.Snapshot, fileName).AsSpan().SequenceEqual(original), result.Snapshot.Id);
            Record("Write the remap", result.WrittenFiles.SequenceEqual([fileName]), string.Join(", ", result.WrittenFiles));
            RecordCleanup(result);
            Record("Eject", result.EjectError is null, result.EjectError ?? "");
            return (result.Snapshot, true);
        }
        catch (ApplyRefusedException refused)
        {
            Record("Write the remap", false, string.Join(" ", refused.Reasons));
            return (null, false);
        }
        catch (ApplyFailedException failed)
        {
            // Part of the write may have landed, so the snapshot is still restored.
            Record("Write the remap", false, failed.InnerException?.Message ?? failed.Message);
            return (failed.Snapshot, false);
        }
    }

    private async Task CheckKeyboardUsesChange(string fileName, byte[] original, Remap remap)
    {
        await CloseDriveAsync("load the change", cancellationToken);
        RecordKey($"Keyboard sends {remap.Action} from {remap.Location}", remap, expectNewAction: true);

        var drive = await ReopenDriveAsync(cancellationToken);
        var expected = LayoutFile.Parse(original).Add(remap).ToBytes();
        var kept = drive.ReadFile(fileName);
        Record("Keyboard kept the file as written", kept.AsSpan().SequenceEqual(expected),
            kept.AsSpan().SequenceEqual(expected) ? "" : $"It now reads: {Prompts.Visible(Encoding.Latin1.GetString(kept))}");
    }

    private async Task RestoreAndCheck(string fileName, byte[] original, Snapshot before, Remap remap)
    {
        Console.WriteLine();
        Console.WriteLine($"Restoring {fileName} from snapshot {before.Id}.");
        try
        {
            var drive = await Prompts.WaitForDriveAsync(platform, restoreCancellationToken);
            var result = await writer.ApplyAsync(drive, writer.PlanRestore(drive, before), $"Self-test: restore {before.Id}",
                new ConsoleProgress(), restoreCancellationToken);
            Record("Restore the snapshot", result.WrittenFiles.SequenceEqual([fileName]), string.Join(", ", result.WrittenFiles));
            RecordCleanup(result);
            Record("Eject after restoring", result.EjectError is null, result.EjectError ?? "");

            await CloseDriveAsync("load the restored layout", restoreCancellationToken);
            RecordKey($"{remap.Location} is back to normal", remap, expectNewAction: false);

            drive = await ReopenDriveAsync(restoreCancellationToken);
            Record($"{fileName} matches the original byte for byte", drive.ReadFile(fileName).AsSpan().SequenceEqual(original), "");

            await platform.EjectAsync(drive.Root, restoreCancellationToken);
            await CloseDriveAsync("finish", restoreCancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ApplyRefusedException or ApplyFailedException or IOException)
        {
            Record("Restore the snapshot", false,
                $"{exception.Message} Restore by hand with: rewire-device-check restore {before.Id}");
        }
    }

    // Ejecting does not close the v-drive: the keyboard offers it again within seconds, and only
    // Program+F1 closes it. Waiting for both means each prompt matches what the keyboard is doing.
    private async Task CloseDriveAsync(string purpose, CancellationToken token)
    {
        var detector = new VDriveDetector(platform);
        Event("Waiting for the keyboard to offer the v-drive again after the eject.");
        if (await detector.WaitForReturnAsync(ReturnTimeout, token) is null)
        {
            Event($"The v-drive did not come back within {ReturnTimeout.TotalSeconds:0} seconds.");
            Prompts.Pause($"If the keyboard still has the v-drive open, press Program+F1 once to close it and {purpose}.");
        }
        else
        {
            Event($"The v-drive is back. Press Program+F1 once to close it and {purpose}.");
            await detector.WaitUntilClosedAsync(token);
            Event("The v-drive closed.");
        }

        await Task.Delay(LoadDelay, token);
    }

    private async Task<VDrive> ReopenDriveAsync(CancellationToken token)
    {
        Event("Press Program+F1 once to reopen the v-drive.");
        var drive = await new VDriveDetector(platform).WaitForDriveAsync(token);
        Event($"The v-drive is open at {drive.Root}.");
        return drive;
    }

    // A single-character action, such as a letter, can be checked by having the user press
    // the key here; anything else has to be judged by the user.
    private void RecordKey(string step, Remap remap, bool expectNewAction)
    {
        if (remap.Action.Name.Length != 1)
        {
            var answer = Prompts.YesOrNo(expectNewAction
                ? $"Press the {remap.Location} key. Did it do {remap.Action}?"
                : $"Press the {remap.Location} key. Did it do its usual job, not {remap.Action}?");
            Record(step, answer, $"answered at {Now()}");
            return;
        }

        var typed = Prompts.ReadLine($"Press the {remap.Location} key, then Enter: ");
        var sentAction = typed.Contains(remap.Action.Name, StringComparison.OrdinalIgnoreCase);
        Record(step, sentAction == expectNewAction, $"typed \"{Prompts.Visible(typed)}\" at {Now()}");
    }

    private static void Event(string message) => Console.WriteLine($"{Now()}  {message}");

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    private void RecordCleanup(ApplyResult result) =>
        Record("Remove macOS system files", result.LeftoverSystemFiles.Count == 0,
            result.LeftoverSystemFiles.Count == 0 ? "" : $"Could not remove {string.Join(", ", result.LeftoverSystemFiles)}");

    private bool Record(string step, bool passed, string detail)
    {
        _results.Add((step, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? $": {detail}" : "")}");
        return passed;
    }

    private int Summarise()
    {
        var failed = _results.Count(result => !result.Passed);
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"All {_results.Count} checks passed."
            : $"{failed} of {_results.Count} checks failed:");
        foreach (var (step, _, detail) in _results.Where(result => !result.Passed))
            Console.WriteLine($"  {step}{(detail.Length > 0 ? $": {detail}" : "")}");
        return failed == 0 ? 0 : 1;
    }
}
