using ClipFx.Engine;
using ClipFx.Platform;
using Xunit;

namespace ClipFx.Tests;

public class UnattributedTests
{
    static InterfaceSample Wifi(long inB, long outB) => new("wifi", InterfaceKind.Physical, inB, outB);
    static InterfaceSample Wsl(long inB, long outB) => new("wsl", InterfaceKind.VirtualMachine, inB, outB);

    // Feed samples, settling after each; returns the summed result.
    static UnattributedTracker.Result Run(UnattributedTracker t, IEnumerable<(InterfaceSample[] s, long aUp, long aDown, long lUp, long lDown)> steps)
    {
        long ou = 0, od = 0, vu = 0, vd = 0;
        foreach (var (s, aUp, aDown, lUp, lDown) in steps)
        {
            t.Sample(s);
            var r = t.Settle(aUp, aDown, lUp, lDown);
            ou += r.OtherUp; od += r.OtherDown; vu += r.VmUp; vd += r.VmDown;
        }
        return new(ou, od, vu, vd);
    }

    static IEnumerable<(InterfaceSample[], long, long, long, long)> Hold(InterfaceSample[] s, long aUp, long aDown, long lUp = 0, long lDown = 0, int times = 4)
        => Enumerable.Repeat((s, aUp, aDown, lUp, lDown), times);

    [Fact]
    public void ReportsBytesNoProcessClaims()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(5_000, 5_000)]); // baseline: earlier traffic isn't ours to count
        var r = Run(t, Hold([Wifi(5_000 + 1_040_000, 5_000 + 20_000)], aUp: 15_000, aDown: 1_000_000));
        Assert.Equal(40_000, r.OtherDown);
        Assert.Equal(5_000, r.OtherUp);
    }

    [Fact]
    public void LateProcessEventsAreNotCountedTwice()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(0, 0)]);
        // The adapter sees 1 MB now; the per-process events for it arrive a second later.
        var r1 = t.Settle(0, 0, 0, 0);
        t.Sample([Wifi(1_000_000, 0)]);
        var r2 = t.Settle(0, 0, 0, 0);
        t.Sample([Wifi(1_000_000, 0)]);
        var r3 = t.Settle(0, 1_000_000, 0, 0);
        var rest = Run(t, Hold([Wifi(1_000_000, 0)], 0, 1_000_000));
        Assert.Equal(0, r1.OtherDown + r2.OtherDown + r3.OtherDown + rest.OtherDown);
    }

    [Fact]
    public void LocalNetworkCopiesAreNotData()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(0, 0)]);
        var r = Run(t, Hold([Wifi(50_000_000, 0)], 0, 0, lUp: 0, lDown: 50_000_000));
        Assert.Equal(0, r.OtherDown);
    }

    [Fact]
    public void CounterResetCountsFromZero()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(9_000_000, 0)]);
        t.Sample([Wifi(9_500_000, 0)]); // +500 KB
        t.Sample([Wifi(300_000, 0)]);   // reconnected: +300 KB
        var r = Run(t, Hold([Wifi(300_000, 0)], 0, 0));
        Assert.Equal(800_000, r.OtherDown);
    }

    [Fact]
    public void AdapterThatAppearsLaterCountsInFull()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(1_000, 1_000)]);
        var r = Run(t, Hold([Wifi(1_000, 1_000), new InterfaceSample("usb-tether", InterfaceKind.Physical, 700_000, 0)], 0, 0));
        Assert.Equal(700_000, r.OtherDown);
    }

    [Fact]
    public void VirtualMachineTrafficIsSplitOut()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(0, 0), Wsl(0, 0)]);
        // WSL downloads 2 MB (host vSwitch sends it out to the VM), plus 100 KB nobody claims.
        var r = Run(t, Hold([Wifi(2_100_000, 0), Wsl(0, 2_000_000)], 0, 0));
        Assert.Equal(2_000_000, r.VmDown);
        Assert.Equal(100_000, r.OtherDown);
    }

    [Fact]
    public void LocalOnlyVmTrafficIsNotData()
    {
        var t = new UnattributedTracker();
        t.Sample([Wifi(0, 0), Wsl(0, 0)]);
        // Host <-> WSL file sync never touches Wi-Fi.
        var r = Run(t, Hold([Wifi(0, 0), Wsl(80_000_000, 80_000_000)], 0, 0));
        Assert.Equal(0, r.VmDown + r.VmUp + r.OtherDown + r.OtherUp);
    }
}
