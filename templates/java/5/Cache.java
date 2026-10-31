public final class Cache<K, V> {
    private record Entry<V>(V value, Instant expires) {}

    private final Map<K, Entry<V>> items = new HashMap<>();
    private final Duration lifetime;

    public Cache(Duration lifetime) {
        this.lifetime = lifetime;
    }

    public V getOrAdd(K key, Function<K, V> create) {
        Entry<V> entry = items.get(key);
        if (entry != null && entry.expires().isAfter(Instant.now())) {
            return entry.value();
        }

        V value = create.apply(key);
        items.put(key, new Entry<>(value, Instant.now().plus(lifetime)));
        return value;
    }

    public int removeExpired() {
        int before = items.size();
        items.values().removeIf(entry -> !entry.expires().isAfter(Instant.now()));
        return before - items.size();
    }
}
