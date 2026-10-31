using System.Diagnostics;
using System.Runtime.Versioning;

namespace Rewire.Device.Platform;

[SupportedOSPlatform("macos")]
internal sealed class MacDrivePlatform : IDrivePlatform
{
    private static readonly TimeSpan UnmountTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<MountedVolume> MountedVolumes() =>
        Directory.EnumerateDirectories("/Volumes")
            .Select(root => new MountedVolume(root, Path.GetFileName(root)))
            .ToList();

    public IReadOnlyList<string> RemoveSystemFiles(string root) => SystemFiles.RemoveFrom(root);

    public async Task EjectAsync(string root, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("diskutil")
        {
            ArgumentList = { "eject", root },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(start)
            ?? throw new IOException("Could not start diskutil.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new IOException($"diskutil could not eject {root}: {(await error).Trim()}");

        await WaitUntilGoneAsync(root, cancellationToken);
    }

    private static async Task WaitUntilGoneAsync(string root, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (Directory.Exists(root))
        {
            if (deadline.Elapsed > UnmountTimeout)
                throw new IOException($"{root} was still mounted {UnmountTimeout.TotalSeconds:0} seconds after ejecting.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }
}
