using System.Collections;
using ChickenCommander.Core.Viewer;

namespace ChickenCommander.Viewer;

/// <summary>A displayed line. Wrapping the text gives each row an identity (and its index) for scrolling.</summary>
public sealed record ViewerLine(long Index, string Text);

/// <summary>
/// Read-only list that WPF's virtualizing panel pulls rows from on demand: text lines or hex rows of a
/// <see cref="FileDocument"/>. Recently used rows are cached so scrolling back and forth stays smooth.
/// </summary>
public sealed class VirtualLines(FileDocument document, bool hex) : IList
{
    private const int CacheSize = 2048;
    private readonly Dictionary<int, ViewerLine> cache = [];
    private readonly Queue<int> order = new();

    public int Count => hex ? (int)Math.Min(document.HexLineCount, int.MaxValue) : document.LineCount;

    public object? this[int index]
    {
        get
        {
            if (cache.TryGetValue(index, out var line)) return line;
            line = new ViewerLine(index, hex ? document.GetHexLine(index) : document.GetLine(index));
            cache[index] = line;
            order.Enqueue(index);
            if (order.Count > CacheSize) cache.Remove(order.Dequeue());
            return line;
        }
        set => throw new NotSupportedException();
    }

    public int IndexOf(object? value) => value is ViewerLine l ? (int)l.Index : -1;
    public bool Contains(object? value) => value is ViewerLine l && l.Index < Count;

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
}
