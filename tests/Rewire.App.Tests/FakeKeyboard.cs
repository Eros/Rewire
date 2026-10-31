using System.Text;
using Rewire.Device.Platform;

namespace Rewire.App.Tests;

/// <summary>
/// A v-drive in a temporary folder that behaves like the real keyboard: after an eject it offers
/// the drive again, and it only goes away when the test "presses Program+F1".
/// </summary>
internal sealed class FakeKeyboard : IDrivePlatform, IDisposable
{
    private volatile bool _open = true;

    public FakeKeyboard()
    {
        Root = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "active"));
        Directory.CreateDirectory(Path.Combine(Root, "firmware"));
    }

    public string Root { get; }

    public int Ejects { get; private set; }

    public void Write(string fileName, string text) =>
        File.WriteAllBytes(Path.Combine(Root, "active", fileName), Encoding.Latin1.GetBytes(text));

    public string Read(string fileName) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Root, "active", fileName)));

    public void PressProgramF1() => _open = !_open;

    public IReadOnlyList<MountedVolume> MountedVolumes() => _open ? [new MountedVolume(Root, "ADVANTAGE2")] : [];

    public IReadOnlyList<string> RemoveSystemFiles(string root) => [];

    public Task EjectAsync(string root, CancellationToken cancellationToken)
    {
        Ejects++;
        return Task.CompletedTask;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
