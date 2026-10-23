using ClipFx.Share;
using Xunit;

namespace ClipFx.Share.Tests;

public class RangeSetTests
{
    [Fact]
    public void MergesAndFindsGaps()
    {
        var r = new RangeSet();
        r.Add(10, 20); r.Add(30, 40); r.Add(20, 30); r.Add(50, 60); r.Add(0, 5);
        Assert.Equal("0-5,10-40,50-60", string.Join(",", r.ToArray().Select(x => $"{x[0]}-{x[1]}")));
        Assert.Equal(45, r.Covered);
        Assert.Equal(new List<(long, long)> { (5, 10), (40, 50), (60, 100) }, r.Missing(100));
        Assert.True(r.Covers(12, 38));
        Assert.False(r.Covers(38, 52));
        r.Add(0, 100);
        Assert.True(r.IsComplete(100));
    }
}

public class PathSafetyTests
{
    [Theory]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("/etc/passwd", "etc/passwd")]
    [InlineData("a/../../b.txt", "a/b.txt")]
    [InlineData("C:\\Windows\\x.dll", "C_/Windows/x.dll")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trailing. ", "trailing")]
    public void StaysInsideRoot(string input, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        var full = PathSafety.Combine(root, input);
        Assert.StartsWith(Path.GetFullPath(root), full);
        Assert.Equal(Path.Combine([Path.GetFullPath(root), .. expected.Split('/')]), full);
    }

    [Fact]
    public void UniqueNames()
    {
        var taken = new HashSet<string> { "/x/a.txt", "/x/a (2).txt" };
        Assert.Equal("/x/a (3).txt", PathSafety.Unique("/x/a.txt", taken.Contains).Replace('\\', '/'));
    }
}
