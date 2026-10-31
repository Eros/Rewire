using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Rewire.Device.Platform;

/// <remarks>
/// Ejects by locking, dismounting and ejecting the volume's media rather than with
/// CM_Request_Device_Eject. The v-drive shares one USB device with the keyboard itself,
/// and removing the media touches only the drive.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDrivePlatform : IDrivePlatform
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804;
    private const uint IoctlStorageEjectMedia = 0x002D4808;

    private const int LockAttempts = 20;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(500);

    public IReadOnlyList<MountedVolume> MountedVolumes() =>
        DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady)
            .Select(drive => new MountedVolume(drive.RootDirectory.FullName, drive.VolumeLabel))
            .ToList();

    public IReadOnlyList<string> RemoveSystemFiles(string root) => [];

    public async Task EjectAsync(string root, CancellationToken cancellationToken)
    {
        var volumePath = $@"\\.\{Path.GetPathRoot(root)!.TrimEnd('\\')}";
        using var volume = CreateFile(volumePath, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not open {root} to eject it.");

        // Another program, such as an antivirus scanner, may briefly hold a file open.
        for (var attempt = 1; !Control(volume, FsctlLockVolume); attempt++)
        {
            if (attempt == LockAttempts)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{root} is in use by another program.");
            await Task.Delay(LockRetryDelay, cancellationToken);
        }

        if (!Control(volume, FsctlDismountVolume))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not dismount {root}.");

        byte preventRemoval = 0;
        if (!Control(volume, IoctlStorageMediaRemoval, ref preventRemoval))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not allow removal of {root}.");

        if (!Control(volume, IoctlStorageEjectMedia))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not eject {root}.");
    }

    private static bool Control(SafeFileHandle device, uint code)
    {
        byte none = 0;
        return DeviceIoControl(device, code, ref none, 0, 0, 0, out _, 0);
    }

    private static bool Control(SafeFileHandle device, uint code, ref byte input) =>
        DeviceIoControl(device, code, ref input, 1, 0, 0, out _, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, ref byte input, uint inputSize,
        nint output, uint outputSize, out uint bytesReturned, nint overlapped);
}
