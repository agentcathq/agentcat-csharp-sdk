using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace AgentCat.Internal.Collections;

// A dictionary that enumerates in insertion order, also after removals, like the keys of a JS object: a new key goes
// last, and setting an existing key keeps its place. Dictionary<,> reuses the slot of a removed key, so a key added
// after a removal can come earlier.
internal sealed class InsertionOrderedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _values;
    private readonly List<TKey> _order = new();

    internal InsertionOrderedDictionary(IEqualityComparer<TKey> comparer) => _values = new Dictionary<TKey, TValue>(comparer);

    public int Count => _order.Count;

    public bool IsReadOnly => false;

    public ICollection<TKey> Keys => new ReadOnlyCollection<TKey>(_order.ToArray());

    public ICollection<TValue> Values
    {
        get
        {
            var values = new TValue[_order.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = _values[_order[i]];
            }

            return new ReadOnlyCollection<TValue>(values);
        }
    }

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public TValue this[TKey key]
    {
        get => _values[key];
        set
        {
            if (!_values.ContainsKey(key))
            {
                _order.Add(key);
            }

            _values[key] = value;
        }
    }

    public void Add(TKey key, TValue value)
    {
        _values.Add(key, value);
        _order.Add(key);
    }

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public bool ContainsKey(TKey key) => _values.ContainsKey(key);

    public bool Contains(KeyValuePair<TKey, TValue> item)
        => _values.TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

#if NETSTANDARD2_0
    public bool TryGetValue(TKey key, out TValue value) => _values.TryGetValue(key, out value);
#else
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _values.TryGetValue(key, out value);
#endif

    public bool Remove(TKey key)
    {
        if (!_values.Remove(key))
        {
            return false;
        }

        _order.RemoveAt(IndexOf(key));
        return true;
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);

    public void Clear()
    {
        _values.Clear();
        _order.Clear();
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        _ = array ?? throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0 || arrayIndex > array.Length - _order.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }

        foreach (var key in _order)
        {
            array[arrayIndex++] = new KeyValuePair<TKey, TValue>(key, _values[key]);
        }
    }

    // The key list's enumerator throws when a key is added or removed during the enumeration.
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<TKey, TValue>(key, _values[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int IndexOf(TKey key)
    {
        var comparer = _values.Comparer;
        for (var i = 0; i < _order.Count; i++)
        {
            if (comparer.Equals(_order[i], key))
            {
                return i;
            }
        }

        return -1;
    }
}
