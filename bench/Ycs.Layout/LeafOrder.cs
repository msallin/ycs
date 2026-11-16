namespace Ycs.Layout;

/// <summary>A position in a <see cref="LeafOrder"/>: a leaf and an index in it. Any insertion makes it stale.</summary>
internal record struct Cursor(int Leaf, int Index);

/// <summary>
/// A client's structs in clock order, in leaves of at most <c>capacity</c> structs with the first start of each leaf
/// beside them (the leaf order ycs builds, design section 1.3, without its run-head index). A full leaf splits in two
/// before an insertion; leaves never merge (nothing here merges). With a capacity above any count it is one flat leaf,
/// where an insertion shifts every later entry, as <see cref="ClientOrder"/>.
/// </summary>
internal sealed class LeafOrder
{
    private sealed class Leaf(int size)
    {
        public uint[] Starts = new uint[size];
        public int[] Handles = new int[size];
        public int Count;

        public void Grow()
        {
            Array.Resize(ref Starts, Starts.Length * 2);
            Array.Resize(ref Handles, Handles.Length * 2);
        }
    }

    private readonly int _capacity;
    private Leaf[] _leaves = new Leaf[1];
    private uint[] _first = new uint[1];
    private int _count;

    public LeafOrder(int capacity, ClientOrder from)
    {
        _capacity = capacity;
        for (var i = 0; i < from.Count; i++)
        {
            Add(from.Starts[i], from.Handles[i]);
        }
    }

    public int LastHandle => _leaves[_count - 1].Handles[_leaves[_count - 1].Count - 1];

    public int Handle(Cursor c) => _leaves[c.Leaf].Handles[c.Index];

    /// <summary>The position of the struct that holds <paramref name="clock"/>, which the order holds.</summary>
    public Cursor Find(uint clock)
    {
        var l = _count - 1;
        if (clock < _first[l])
        {
            int lo = 0, hi = l - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (_first[mid] <= clock)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            l = lo;
        }

        var leaf = _leaves[l];
        var starts = leaf.Starts;
        int a = 0, b = leaf.Count - 1;
        while (a < b)
        {
            var mid = (a + b + 1) >> 1;
            if (starts[mid] <= clock)
            {
                a = mid;
            }
            else
            {
                b = mid - 1;
            }
        }

        return new(l, a);
    }

    /// <summary>Moves to the next position; false at the end.</summary>
    public bool Next(ref Cursor c)
    {
        if (++c.Index < _leaves[c.Leaf].Count)
        {
            return true;
        }

        c = new(c.Leaf + 1, 0);
        return c.Leaf < _count;
    }

    public void Add(uint start, int handle)
    {
        if (_count == 0 || _leaves[_count - 1].Count == _capacity)
        {
            OpenLeaf(_count, new Leaf(Math.Min(_capacity, 4)), start);
        }

        var leaf = _leaves[_count - 1];
        if (leaf.Count == leaf.Starts.Length)
        {
            leaf.Grow();
        }

        leaf.Starts[leaf.Count] = start;
        leaf.Handles[leaf.Count++] = handle;
    }

    /// <summary>Inserts a struct right after <paramref name="c"/>; returns the new struct's position.</summary>
    public Cursor InsertAfter(Cursor c, uint start, int handle)
    {
        var l = c.Leaf;
        var at = c.Index + 1;
        var leaf = _leaves[l];
        if (leaf.Count == _capacity)
        {
            var half = leaf.Count / 2;
            var right = new Leaf(_capacity) { Count = leaf.Count - half };
            Array.Copy(leaf.Starts, half, right.Starts, 0, right.Count);
            Array.Copy(leaf.Handles, half, right.Handles, 0, right.Count);
            leaf.Count = half;
            OpenLeaf(l + 1, right, right.Starts[0]);
            if (at > half)
            {
                (l, at, leaf) = (l + 1, at - half, right);
            }
        }
        else if (leaf.Count == leaf.Starts.Length)
        {
            leaf.Grow();
        }

        Array.Copy(leaf.Starts, at, leaf.Starts, at + 1, leaf.Count - at);
        Array.Copy(leaf.Handles, at, leaf.Handles, at + 1, leaf.Count - at);
        leaf.Starts[at] = start;
        leaf.Handles[at] = handle;
        leaf.Count++;
        return new(l, at);
    }

    private void OpenLeaf(int at, Leaf leaf, uint first)
    {
        if (_count == _leaves.Length)
        {
            Array.Resize(ref _leaves, _count * 2);
            Array.Resize(ref _first, _count * 2);
        }

        Array.Copy(_leaves, at, _leaves, at + 1, _count - at);
        Array.Copy(_first, at, _first, at + 1, _count - at);
        _leaves[at] = leaf;
        _first[at] = first;
        _count++;
    }

    /// <summary>The order as a <see cref="ClientOrder"/>, for the checks.</summary>
    public ClientOrder ToClientOrder()
    {
        var order = new ClientOrder();
        for (var l = 0; l < _count; l++)
        {
            for (var i = 0; i < _leaves[l].Count; i++)
            {
                order.Add(_leaves[l].Starts[i], _leaves[l].Handles[i]);
            }
        }

        return order;
    }
}
