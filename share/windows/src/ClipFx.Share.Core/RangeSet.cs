namespace ClipFx.Share;

/// <summary>Sorted, merged set of half-open byte ranges [start, end).</summary>
public sealed class RangeSet
{
    readonly List<(long S, long E)> _r = [];
    readonly object _gate = new();

    public RangeSet() { }
    public RangeSet(IEnumerable<long[]>? ranges) { if (ranges != null) foreach (var x in ranges) if (x.Length == 2) Add(x[0], x[1]); }

    public void Add(long s, long e)
    {
        if (e <= s) return;
        lock (_gate)
        {
            int i = 0;
            while (i < _r.Count && _r[i].E < s) i++;
            int j = i;
            while (j < _r.Count && _r[j].S <= e) { s = Math.Min(s, _r[j].S); e = Math.Max(e, _r[j].E); j++; }
            _r.RemoveRange(i, j - i);
            _r.Insert(i, (s, e));
        }
    }

    public long Covered { get { lock (_gate) return _r.Sum(x => x.E - x.S); } }

    public bool Covers(long s, long e)
    {
        lock (_gate) return _r.Any(x => x.S <= s && x.E >= e);
    }

    public bool IsComplete(long size) => size == 0 || Covers(0, size);

    /// <summary>Gaps within [0, size).</summary>
    public List<(long S, long E)> Missing(long size)
    {
        var gaps = new List<(long, long)>();
        long pos = 0;
        lock (_gate)
        {
            foreach (var (s, e) in _r)
            {
                if (s >= size) break;
                if (s > pos) gaps.Add((pos, s));
                pos = Math.Max(pos, e);
            }
        }
        if (pos < size) gaps.Add((pos, size));
        return gaps;
    }

    public long[][] ToArray() { lock (_gate) return _r.Select(x => new[] { x.S, x.E }).ToArray(); }
}
