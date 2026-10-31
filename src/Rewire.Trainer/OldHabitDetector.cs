using Rewire.Core.Keyboards;

namespace Rewire.Trainer;

/// <summary>
/// Recognises a wrong key that sits where the intended key would be on the user's old, staggered keyboard,
/// such as reaching top-right for Backspace. It repeats the finger's old reach from its home key on the new board
/// and checks whether that is where the user landed.
/// </summary>
public sealed class OldHabitDetector
{
    // Far enough to catch a reach that overshoots the new board's edge, near enough not to blame a mere slip.
    private const double MaximumDistance = 1.6;

    private static readonly Dictionary<(Hand, Finger), string> HomeKeys = new()
    {
        [(Hand.Left, Finger.Pinky)] = "a",
        [(Hand.Left, Finger.Ring)] = "s",
        [(Hand.Left, Finger.Middle)] = "d",
        [(Hand.Left, Finger.Index)] = "f",
        [(Hand.Right, Finger.Index)] = "j",
        [(Hand.Right, Finger.Middle)] = "k",
        [(Hand.Right, Finger.Ring)] = "l",
        [(Hand.Right, Finger.Pinky)] = ";",
    };

    private readonly KeyboardPack _oldBoard;
    private readonly KeyboardPack _newBoard;
    private readonly Dictionary<string, string> _oldKeysByAction;

    public OldHabitDetector(KeyboardPack oldBoard, KeyboardPack newBoard)
    {
        _oldBoard = oldBoard;
        _newBoard = newBoard;
        _oldKeysByAction = oldBoard.Layers[0].Keys
            .GroupBy(pair => pair.Value.Action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
    }

    public KeyboardPack OldBoard => _oldBoard;

    /// <summary>The physical key on the new board where the old reach for <paramref name="action"/> lands, if any.</summary>
    public string? OldPositionOf(string action)
    {
        if (!_oldKeysByAction.TryGetValue(action, out var oldKeyId))
            return null;

        var oldKey = _oldBoard.Key(oldKeyId);
        if (!HomeKeys.TryGetValue((oldKey.Hand, oldKey.Finger), out var homeId)
            || Find(_oldBoard, homeId) is not { } oldHome
            || Find(_newBoard, homeId) is not { } newHome)
            return null;

        var reach = Centre(oldKey) - Centre(oldHome);
        var landing = Centre(newHome) + reach;

        return _newBoard.Keys
            .Where(key => key.Hand == oldKey.Hand && key.Cluster != Cluster.Thumb && key.Remappable)
            .Select(key => (key.Id, Distance: (Centre(key) - landing).Length()))
            .Where(candidate => candidate.Distance <= MaximumDistance)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// The old board's key for <paramref name="intendedAction"/>, when the wrong key pressed sits where that key
    /// used to be; null for any other mistake.
    /// </summary>
    public string? Classify(string intendedAction, string intendedKeyId, string? pressedKeyId)
    {
        if (pressedKeyId is null || pressedKeyId == intendedKeyId)
            return null;

        return OldPositionOf(intendedAction) == pressedKeyId ? _oldKeysByAction[intendedAction] : null;
    }

    private static PhysicalKey? Find(KeyboardPack pack, string id) => pack.Keys.FirstOrDefault(key => key.Id == id);

    // Thumb clusters are rotated, but only the unrotated finger keys are ever compared here.
    private static System.Numerics.Vector2 Centre(PhysicalKey key) =>
        new((float)(key.X + key.Width / 2), (float)(key.Y + key.Height / 2));
}
