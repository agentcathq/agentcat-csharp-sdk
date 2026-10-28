using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Primitives;

namespace AgentCat.Internal.Http;

/// <summary>
/// The headers and URL of one HTTP request, copied into strings: lower-case names sorted ordinally, each with one
/// value, and the encoded URL. It never holds the request.
/// </summary>
internal sealed class RequestCapture
{
    private const string CookieHeader = "cookie";

    private RequestCapture(KeyValuePair<string, string>[] headers, string? url)
    {
        Headers = new ReadOnlyCollection<KeyValuePair<string, string>>(headers);
        Url = url;
    }

    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>The encoded URL, or <see langword="null"/> when the request had no host.</summary>
    internal string? Url { get; }

    /// <summary>
    /// Copies every header as given: the name lower-cased, several values joined with <c>"; "</c> for <c>cookie</c>
    /// and <c>", "</c> otherwise, no value as <c>""</c>. When two names are equal after lower-casing, the later one
    /// wins.
    /// </summary>
    internal static RequestCapture Create(IEnumerable<KeyValuePair<string, StringValues>>? headers, string? url)
    {
        if (headers is null)
        {
            return new RequestCapture(Array.Empty<KeyValuePair<string, string>>(), url);
        }

        var entries = new Entry[headers is ICollection<KeyValuePair<string, StringValues>> sized ? sized.Count : 16];
        var count = 0;
        foreach (var header in headers)
        {
            if (header.Key is null)
            {
                continue;
            }

            if (count == entries.Length)
            {
                Array.Resize(ref entries, Math.Max(4, count * 2));
            }

            var name = header.Key.ToLowerInvariant();
            entries[count] = new Entry(name, Join(name, header.Value), count);
            count++;
        }

        Array.Sort(entries, 0, count, EntryOrder.Instance);
        var unique = 0;
        for (var i = 0; i < count; i++)
        {
            if (i + 1 < count && string.Equals(entries[i].Name, entries[i + 1].Name, StringComparison.Ordinal))
            {
                continue;
            }

            entries[unique++] = entries[i];
        }

        var pairs = new KeyValuePair<string, string>[unique];
        for (var i = 0; i < unique; i++)
        {
            pairs[i] = new KeyValuePair<string, string>(entries[i].Name, entries[i].Value);
        }

        return new RequestCapture(pairs, url);
    }

    // StringValues.ToString() joins with "," and matches neither fetch nor the other SDKs.
    private static string Join(string name, StringValues values)
    {
        switch (values.Count)
        {
            case 0:
                return string.Empty;
            case 1:
                return values[0] ?? string.Empty;
            default:
                var separator = string.Equals(name, CookieHeader, StringComparison.Ordinal) ? "; " : ", ";
                return string.Join(separator, values.ToArray());
        }
    }

    private readonly struct Entry
    {
        internal Entry(string name, string value, int index)
        {
            Name = name;
            Value = value;
            Index = index;
        }

        internal string Name { get; }

        internal string Value { get; }

        internal int Index { get; }
    }

    // By name, then by arrival, so the last of equal names is the later header.
    private sealed class EntryOrder : IComparer<Entry>
    {
        internal static readonly EntryOrder Instance = new();

        public int Compare(Entry x, Entry y)
        {
            var byName = string.CompareOrdinal(x.Name, y.Name);
            return byName != 0 ? byName : x.Index.CompareTo(y.Index);
        }
    }
}
