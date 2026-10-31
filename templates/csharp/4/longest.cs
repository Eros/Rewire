var words = new List<string> { "keyboard", "thumb", "column", "remap" };
var longest = words
    .Where(word => word.Length > 4)
    .OrderByDescending(word => word.Length)
    .Select(word => word.ToUpperInvariant())
    .FirstOrDefault() ?? "none";
Dictionary<string, int> lengths = words.ToDictionary(word => word, word => word.Length);
