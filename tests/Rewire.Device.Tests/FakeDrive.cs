using System.Text;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;

namespace Rewire.Device.Tests;

/// <summary>A v-drive laid out in a temporary folder.</summary>
internal sealed class FakeDrive : IDisposable
{
    public FakeDrive()
    {
        Root = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "active"));
        Directory.CreateDirectory(Path.Combine(Root, "firmware"));
    }

    public string Root { get; }

    public string Active => Path.Combine(Root, "active");

    public MountedVolume Volume => new(Root, "ADV2");

    public VDrive Open() => VDrive.TryOpen(Volume)!;

    public void Write(string relativePath, string text) =>
        File.WriteAllBytes(Path.Combine(Root, relativePath), Encoding.Latin1.GetBytes(text));

    public string Read(string relativePath) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Root, relativePath)));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class FakePlatform(params MountedVolume[] volumes) : IDrivePlatform
{
    public MountedVolume[] Volumes { get; set; } = volumes;

    public List<string> Ejected { get; } = [];

    public List<string> Cleaned { get; } = [];

    public Exception? EjectFailure { get; init; }

    public IReadOnlyList<MountedVolume> MountedVolumes() => Volumes;

    public IReadOnlyList<string> Leftovers { get; init; } = [];

    public IReadOnlyList<string> RemoveSystemFiles(string root)
    {
        Cleaned.Add(root);
        return Leftovers;
    }

    public Task EjectAsync(string root, CancellationToken cancellationToken)
    {
        if (EjectFailure is not null)
            throw EjectFailure;
        Ejected.Add(root);
        return Task.CompletedTask;
    }
}
