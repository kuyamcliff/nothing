using System.Net;
using ClipFx.Engine;
using ClipFx.Model;
using Xunit;

namespace ClipFx.Tests;

public class AddressFilterTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("192.168.43.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("142.250.72.14", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:8.8.4.4", true)]
    public void Classifies(string ip, bool internet) =>
        Assert.Equal(internet, AddressFilter.IsInternet(IPAddress.Parse(ip)));

    [Fact]
    public void UnknownAddressCounts() => Assert.True(AddressFilter.IsInternet(null));
}

public class PeriodTests
{
    static Settings S(string p, int day = 1) => new() { ResetPeriod = p, ResetDay = day };

    [Fact]
    public void Daily()
    {
        var now = new DateTime(2026, 10, 22, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 10, 22), Period.CurrentStart(now, S("daily")));
        Assert.Equal(new DateTime(2026, 10, 23), Period.NextReset(now, S("daily")));
    }

    [Fact]
    public void WeeklyStartsMonday()
    {
        var thursday = new DateTime(2026, 10, 22, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 19), Period.CurrentStart(thursday, S("weekly")));
        var monday = new DateTime(2026, 10, 19, 0, 0, 1);
        Assert.Equal(new DateTime(2026, 10, 19), Period.CurrentStart(monday, S("weekly")));
        var sunday = new DateTime(2026, 10, 25, 23, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 19), Period.CurrentStart(sunday, S("weekly")));
    }

    [Fact]
    public void MonthlyOnResetDay()
    {
        Assert.Equal(new DateTime(2026, 10, 15), Period.CurrentStart(new DateTime(2026, 10, 22), S("monthly", 15)));
        Assert.Equal(new DateTime(2026, 9, 25), Period.CurrentStart(new DateTime(2026, 10, 22), S("monthly", 25)));
        Assert.Equal(new DateTime(2026, 12, 28), Period.CurrentStart(new DateTime(2027, 1, 5), S("monthly", 31)));
        Assert.Equal(new DateTime(2026, 11, 25), Period.NextReset(new DateTime(2026, 10, 30), S("monthly", 25)));
    }

    [Fact]
    public void Never() => Assert.Null(Period.CurrentStart(DateTime.Now, S("never")));
}
