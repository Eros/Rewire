using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;

namespace Rewire.ViewModels;

/// <summary>
/// Guides the user through each moment that needs the keyboard: opening the v-drive, writing,
/// and closing it again with Program+F1 so the keyboard loads the result.
/// </summary>
public sealed partial class DeviceFlowViewModel(IDrivePlatform platform, SnapshotStore snapshots) : ViewModelBase
{
    private const string OpenDriveInstruction =
        "Open the v-drive: hold Program and press F1. If nothing happens, press Program+Shift+Esc first to turn on Power User Mode.";

    private CancellationTokenSource? _cancel;

    /// <summary>How long to wait for the keyboard to offer the drive again after an eject.</summary>
    public TimeSpan ReturnTimeout { get; set; } = TimeSpan.FromSeconds(15);

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    [ObservableProperty]
    public partial bool Succeeded { get; private set; }

    public async Task<IReadOnlyDictionary<string, byte[]>?> ImportAsync()
    {
        return await RunAsync<IReadOnlyDictionary<string, byte[]>>(async token =>
        {
            var drive = await OpenDriveAsync(token);
            var files = drive.LayoutFileNames().ToDictionary(name => name, drive.ReadFile, StringComparer.OrdinalIgnoreCase);
            Finish($"Read {Count(files.Count, "layout")} from the keyboard. Press Program+F1 when you want to close the v-drive.");
            return files;
        });
    }

    /// <param name="plan">The files to write, worked out once the drive is open.</param>
    /// <param name="written">Called with the files written, as soon as they are on the keyboard.</param>
    public async Task<ApplyResult?> ApplyAsync(Func<VDrive, IReadOnlyList<LayoutWrite>> plan, string note, Action<ApplyResult> written)
    {
        return await RunAsync(async token =>
        {
            var drive = await OpenDriveAsync(token);
            Status = "Writing to the keyboard…";

            ApplyResult result;
            try
            {
                var progress = new Progress<ApplyStep>(step => Status = Describe(step));
                result = await new VDriveWriter(platform, snapshots).ApplyAsync(drive, plan(drive), note, progress, token);
            }
            catch (ApplyRefusedException refused)
            {
                Fail("Nothing was written:", refused.Reasons);
                return null;
            }
            catch (ApplyFailedException failed)
            {
                Fail(failed.Message, [failed.InnerException?.Message ?? ""]);
                return null;
            }

            written(result);

            // From here the files are safely written, so stopping only skips the guidance.
            try
            {
                await CloseDriveAsync(result, token);
                Finish(result.WrittenFiles.Count == 0
                    ? "Nothing needed writing; the keyboard already matched."
                    : $"Done. {Count(result.WrittenFiles.Count, "file")} written, and the keyboard is using them.");
            }
            catch (OperationCanceledException)
            {
                Finish("Your changes are written. Press Program+F1 to close the v-drive and load them.");
            }

            return result;
        });
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    private async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T?>> flow) where T : class
    {
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsRunning = true;
        Succeeded = false;
        Problems = [];
        try
        {
            return await flow(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Nothing was written.";
            return null;
        }
        catch (IOException exception)
        {
            Fail("The v-drive could not be read, so nothing was written. Was it closed part way through?", [exception.Message]);
            return null;
        }
        finally
        {
            IsRunning = false;
            _cancel = null;
        }
    }

    private async Task<VDrive> OpenDriveAsync(CancellationToken token)
    {
        var detector = new VDriveDetector(platform);
        if (detector.Find() is { } drive)
            return drive;

        Status = OpenDriveInstruction;
        return await detector.WaitForDriveAsync(token);
    }

    // Ejecting does not close the v-drive: the keyboard offers it again, and only Program+F1 closes it.
    private async Task CloseDriveAsync(ApplyResult result, CancellationToken token)
    {
        var detector = new VDriveDetector(platform);
        if (result.EjectError is not null)
        {
            Status = $"Your changes are written, but ejecting failed ({result.EjectError}). Eject the drive yourself, then press Program+F1.";
            await detector.WaitUntilClosedAsync(token);
            return;
        }

        Status = "Waiting for the keyboard to offer the v-drive again…";
        if (await detector.WaitForReturnAsync(ReturnTimeout, token) is null)
            return;

        Status = "Press Program+F1 once to close the v-drive and load your changes.";
        await detector.WaitUntilClosedAsync(token);
    }

    private void Finish(string message)
    {
        Status = message;
        Succeeded = true;
    }

    private void Fail(string message, IReadOnlyList<string> problems)
    {
        Status = message;
        Problems = problems.Where(problem => problem.Length > 0).ToList();
    }

    private static string Describe(ApplyStep step) => step switch
    {
        ApplyStep.Validating => "Checking the new layouts…",
        ApplyStep.Snapshotting => "Saving a snapshot of the keyboard first…",
        ApplyStep.Writing => "Writing to the keyboard…",
        ApplyStep.Cleaning => "Removing the files macOS leaves on the drive…",
        ApplyStep.Ejecting => "Ejecting the v-drive…",
        _ => step.ToString(),
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
