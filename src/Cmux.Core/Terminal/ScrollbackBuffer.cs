namespace Cmux.Core.Terminal;

/// <summary>
/// A fixed-capacity circular buffer for terminal scrollback lines.
/// Provides O(1) Add and RemoveOldest operations, avoiding the O(n)
/// cost of List&lt;T&gt;.RemoveAt(0). The backing array grows on demand
/// so that panes with little output don't reserve the full capacity.
/// </summary>
public sealed class ScrollbackBuffer<T>
{
    private const int InitialArrayLength = 64;

    private readonly int _capacity;
    private T[] _items;
    private int _head; // Index of the oldest item (always 0 until the ring is full)
    private int _count;

    public int Count => _count;
    public int Capacity => _capacity;

    public ScrollbackBuffer(int capacity)
    {
        _capacity = Math.Max(0, capacity);
        _items = _capacity == 0 ? [] : new T[Math.Min(_capacity, InitialArrayLength)];
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length];
        }
    }

    /// <summary>
    /// Adds an item to the end. If at capacity, the oldest item is overwritten.
    /// </summary>
    public void Add(T item)
    {
        if (_capacity == 0)
            return;

        if (_count < _capacity)
        {
            if (_count == _items.Length)
                Grow();
            _items[_count] = item; // _head is 0 while the ring is not full
            _count++;
        }
        else
        {
            // Full: overwrite the oldest item and advance head.
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
        }
    }

    /// <summary>
    /// Adds all items from the given list.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Copies all items to a new list (in order from oldest to newest).
    /// </summary>
    public List<T> ToList()
    {
        var result = new List<T>(_count);
        for (int i = 0; i < _count; i++)
            result.Add(_items[(_head + i) % _items.Length]);
        return result;
    }

    private void Grow()
    {
        int newLength = Math.Min(_capacity, Math.Max(InitialArrayLength, _items.Length * 2));
        var grown = new T[newLength];
        Array.Copy(_items, 0, grown, 0, _count);
        _items = grown;
    }
}
