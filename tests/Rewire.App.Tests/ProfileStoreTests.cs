using System.Text;
using Rewire.Services;

namespace Rewire.App.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "rewire.db");

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    [Fact]
    public void Imports_new_layouts_with_readable_names_qwerty_first()
    {
        using var store = new ProfileStore(DatabasePath);

        store.Import(new Dictionary<string, byte[]>
        {
            ["2_qwerty.txt"] = Bytes(""),
            ["qwerty.txt"] = Bytes("[caps]>[escape]\r\n"),
            ["dvorak.txt"] = Bytes(""),
        });

        Assert.Equal(["qwerty.txt", "dvorak.txt", "2_qwerty.txt"], store.List().Select(profile => profile.FileName));
        Assert.Equal(["QWERTY", "Dvorak", "Program+2 (QWERTY)"], store.List().Select(profile => profile.Name));
        Assert.All(store.List(), profile => Assert.False(profile.HasChanges));
    }

    [Fact]
    public void Keeps_unapplied_edits_when_importing_again()
    {
        using var store = new ProfileStore(DatabasePath);
        store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("") });
        store.SaveWorking("qwerty.txt", Bytes("[caps]>[escape]\r\n"));

        var outcome = store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("[tab]>[q]\r\n") });

        Assert.Equal(ImportOutcome.KeptLocalChanges, Assert.Single(outcome).Outcome);
        var profile = store.Find("qwerty.txt")!;
        Assert.Equal(Bytes("[caps]>[escape]\r\n"), profile.Working);
        Assert.Equal(Bytes("[tab]>[q]\r\n"), profile.Keyboard);
    }

    [Fact]
    public void Takes_the_keyboard_version_when_there_are_no_local_edits()
    {
        using var store = new ProfileStore(DatabasePath);
        store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("") });

        var outcome = store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("[tab]>[q]\r\n") });

        Assert.Equal(ImportOutcome.Updated, Assert.Single(outcome).Outcome);
        Assert.Equal(Bytes("[tab]>[q]\r\n"), store.Find("qwerty.txt")!.Working);
    }

    [Fact]
    public void Marking_a_file_written_clears_its_changes()
    {
        using var store = new ProfileStore(DatabasePath);
        store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("") });
        store.SaveWorking("qwerty.txt", Bytes("[caps]>[escape]\r\n"));
        Assert.True(store.Find("qwerty.txt")!.HasChanges);

        store.MarkWritten("qwerty.txt", Bytes("[caps]>[escape]\r\n"));

        Assert.False(store.Find("QWERTY.TXT")!.HasChanges);
    }

    [Fact]
    public void Keeps_profiles_between_sessions()
    {
        using (var store = new ProfileStore(DatabasePath))
        {
            store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = Bytes("") });
            store.Rename("qwerty.txt", "Everyday");
        }

        using var reopened = new ProfileStore(DatabasePath);
        Assert.Equal("Everyday", reopened.Find("qwerty.txt")!.Name);
    }

    [Theory]
    [InlineData("qwerty.txt", "QWERTY")]
    [InlineData("dvorak.txt", "Dvorak")]
    [InlineData("a_qwerty.txt", "Program+A (QWERTY)")]
    [InlineData("1_dvorak.txt", "Program+1 (Dvorak)")]
    [InlineData("notes.txt", "notes.txt")]
    public void Names_profiles_after_their_hotkey(string fileName, string name)
    {
        Assert.Equal(name, ProfileStore.DefaultName(fileName));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
