public sealed class Cache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Value, DateTime Expires)> _items = new();
    private readonly TimeSpan _lifetime;

    public Cache(TimeSpan lifetime) => _lifetime = lifetime;

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        if (_items.TryGetValue(key, out var item) && item.Expires > DateTime.UtcNow)
            return item.Value;

        var value = create(key);
        _items[key] = (value, DateTime.UtcNow + _lifetime);
        return value;
    }

    public int RemoveExpired()
    {
        var expired = _items.Where(pair => pair.Value.Expires <= DateTime.UtcNow).Select(pair => pair.Key).ToList();
        foreach (var key in expired)
            _items.Remove(key);
        return expired.Count;
    }
}
