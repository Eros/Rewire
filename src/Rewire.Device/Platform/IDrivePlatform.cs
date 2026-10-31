namespace Rewire.Device.Platform;

public sealed record MountedVolume(string Root, string? Label);

/// <summary>The operating-system side of working with a removable drive.</summary>
public interface IDrivePlatform
{
    IReadOnlyList<MountedVolume> MountedVolumes();

    /// <summary>
    /// Removes the hidden files the operating system leaves behind on a drive it has mounted,
    /// returning any it was not allowed to remove.
    /// </summary>
    IReadOnlyList<string> RemoveSystemFiles(string root);

    /// <summary>Ejects the volume and returns once the operating system no longer has it mounted.</summary>
    Task EjectAsync(string root, CancellationToken cancellationToken);
}

public static class DrivePlatform
{
    public static IDrivePlatform ForCurrentOs()
    {
        if (OperatingSystem.IsMacOS())
            return new MacDrivePlatform();
        if (OperatingSystem.IsWindows())
            return new WindowsDrivePlatform();
        throw new PlatformNotSupportedException("Rewire supports macOS and Windows.");
    }
}
