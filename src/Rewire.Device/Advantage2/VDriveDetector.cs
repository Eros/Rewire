using Rewire.Device.Platform;

namespace Rewire.Device.Advantage2;

/// <remarks>
/// Ejecting from the OS does not close the v-drive: the keyboard offers it again a few seconds
/// later, and only Program+F1 closes it and loads the new layout. After an apply, wait for the
/// drive to return, then for it to close, rather than treating the eject as the close.
/// </remarks>
public sealed class VDriveDetector(IDrivePlatform platform)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public VDrive? Find() =>
        platform.MountedVolumes()
            .Select(VDrive.TryOpen)
            .FirstOrDefault(drive => drive is not null);

    /// <summary>Polls until the user opens the v-drive, or until cancelled.</summary>
    public async Task<VDrive> WaitForDriveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (true)
        {
            if (Find() is { } drive)
                return drive;
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    /// <summary>Waits for the keyboard to offer the drive again after an eject.</summary>
    /// <returns>The drive, or null if it did not come back in time.</returns>
    public async Task<VDrive?> WaitForReturnAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await WaitForDriveAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls until the user closes the v-drive with Program+F1, or until cancelled.</summary>
    public async Task WaitUntilClosedAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (Find() is not null)
            await timer.WaitForNextTickAsync(cancellationToken);
    }
}
