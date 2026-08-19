namespace ZxNext.Core.Model;

/// <summary>
/// A mutable, fixed-capacity ordered list of Next colours (a 4bpp bank slot of 16, or an
/// 8bpp flat folder palette of up to 256). One index is permanently reserved for transparency
/// and is never matched against by real pixel colours.
/// </summary>
public class NextPalette(int capacity, int transparentIndex = 0)
{
    public int Capacity { get; } = capacity;
    public int TransparentIndex { get; } = transparentIndex;

    private readonly NextColor?[] _slots = new NextColor?[capacity];

    public IReadOnlyList<NextColor?> Slots => _slots;

    public int FreeSlotCount => Enumerable.Range(0, Capacity).Count(i => i != TransparentIndex && _slots[i] is null);

    public bool Contains(NextColor color) => IndexOf(color) >= 0;

    public int IndexOf(NextColor color)
    {
        for (var i = 0; i < Capacity; i++)
        {
            if (i != TransparentIndex && _slots[i] == color) return i;
        }
        return -1;
    }

    /// <summary>Adds a colour to the first free non-transparent slot (or returns its existing index). -1 if full.</summary>
    public int TryAdd(NextColor color)
    {
        var existing = IndexOf(color);
        if (existing >= 0) return existing;

        for (var i = 0; i < Capacity; i++)
        {
            if (i == TransparentIndex) continue;
            if (_slots[i] is not null) continue;
            _slots[i] = color;
            return i;
        }
        return -1;
    }

    /// <summary>Directly places a colour at an exact index — used when restoring a palette from a saved project.</summary>
    public void SetAt(int index, NextColor? color) => _slots[index] = color;
}
