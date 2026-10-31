public class Counter
{
    private int _count;

    public int Count => _count;

    public void Increment(int by = 1)
    {
        _count += by;
    }
}
