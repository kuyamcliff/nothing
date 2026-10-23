using ClipFx.Platform;

namespace ClipFx.Engine;

/// <summary>
/// Compares what the network adapters carried with what was attributed to processes,
/// and hands back the difference so no byte goes uncounted.
/// </summary>
public sealed class UnattributedTracker
{
    /// <summary>
    /// Per-process events arrive a little after the adapter counts the bytes, so compare against
    /// adapter totals this many samples old (samples are taken every 250 ms).
    /// </summary>
    public const int LagSamples = 2;

    readonly Dictionary<string, (long In, long Out)> _last = new(StringComparer.Ordinal);
    readonly Queue<Totals> _history = new();
    bool _primed;
    long _assignedUp, _assignedDown, _vmAssignedUp, _vmAssignedDown;

    readonly record struct Totals(long PhysUp, long PhysDown, long VmUp, long VmDown);
    Totals _cum;

    public readonly record struct Result(long OtherUp, long OtherDown, long VmUp, long VmDown);

    /// <summary>Adds the latest adapter readings to running totals. Counter resets and new adapters are handled.</summary>
    public void Sample(IReadOnlyList<InterfaceSample> samples)
    {
        foreach (var s in samples)
        {
            long dIn, dOut;
            if (_last.TryGetValue(s.Id, out var prev))
            {
                // A counter that went backwards was reset (adapter reconnected): it counted up from zero.
                dIn = s.InOctets >= prev.In ? s.InOctets - prev.In : s.InOctets;
                dOut = s.OutOctets >= prev.Out ? s.OutOctets - prev.Out : s.OutOctets;
            }
            else
            {
                // At startup, take a baseline. An adapter that appears later (USB tethering) is new: count it all.
                dIn = _primed ? s.InOctets : 0;
                dOut = _primed ? s.OutOctets : 0;
            }
            _last[s.Id] = (s.InOctets, s.OutOctets);

            if (s.Kind == InterfaceKind.Physical)
                _cum = _cum with { PhysUp = _cum.PhysUp + dOut, PhysDown = _cum.PhysDown + dIn };
            else
                // The host receives what a VM sends out.
                _cum = _cum with { VmUp = _cum.VmUp + dIn, VmDown = _cum.VmDown + dOut };
        }
        _primed = true;
        _history.Enqueue(_cum);
        while (_history.Count > LagSamples + 1) _history.Dequeue();
    }

    /// <summary>
    /// Bytes to add now to "other" and "virtual machines", given everything attributed to processes so far
    /// (internet and local network traffic, cumulative).
    /// </summary>
    public Result Settle(long attributedUp, long attributedDown, long localUp, long localDown)
    {
        if (_history.Count == 0) return default;
        var t = _history.Peek(); // adapter totals from a moment ago

        long unUp = Math.Max(0, t.PhysUp - attributedUp - localUp - _assignedUp - _vmAssignedUp);
        long unDown = Math.Max(0, t.PhysDown - attributedDown - localDown - _assignedDown - _vmAssignedDown);

        // Unattributed bytes are first explained by virtual machine traffic, the rest is "other".
        long vmUp = Math.Min(unUp, Math.Max(0, t.VmUp - _vmAssignedUp));
        long vmDown = Math.Min(unDown, Math.Max(0, t.VmDown - _vmAssignedDown));
        long otherUp = unUp - vmUp, otherDown = unDown - vmDown;

        _vmAssignedUp += vmUp; _vmAssignedDown += vmDown;
        _assignedUp += otherUp; _assignedDown += otherDown;
        return new Result(otherUp, otherDown, vmUp, vmDown);
    }
}
