using System.Text;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;

internal static class Prompts
{
    public static async Task<VDrive> WaitForDriveAsync(IDrivePlatform platform, CancellationToken cancellationToken)
    {
        var detector = new VDriveDetector(platform);
        if (detector.Find() is { } drive)
            return drive;

        Console.WriteLine("Waiting for the v-drive. Press Program+F1 on the keyboard, or Ctrl+C to stop.");
        return await detector.WaitForDriveAsync(cancellationToken);
    }

    public static bool Confirm(string question)
    {
        Console.Write($"{question} Type yes to continue: ");
        return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static bool YesOrNo(string question)
    {
        while (true)
        {
            Console.Write($"{question} [y/n]: ");
            switch (Console.ReadLine()?.Trim().ToLowerInvariant())
            {
                case "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
                case null:
                    throw new OperationCanceledException();
            }
        }
    }

    public static string ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? throw new OperationCanceledException();
    }

    public static void Pause(string instruction)
    {
        Console.Write($"{instruction} Then press Enter here.");
        if (Console.ReadLine() is null)
            throw new OperationCanceledException();
    }

    // Shows the whitespace and control characters that make a line unrecognisable.
    public static string Visible(string text)
    {
        var shown = new StringBuilder();
        foreach (var c in text)
            shown.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\x{(int)c:X2}");
        return shown.ToString();
    }
}
