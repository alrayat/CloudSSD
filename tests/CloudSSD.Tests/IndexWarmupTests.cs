using CloudSSD.Core;
using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class IndexWarmupTests
{
    private static FileStat F(string p, long size) => new(p, size, DateTime.UtcNow);

    [Fact]
    public void Plans_small_files_whole_and_both_ends_of_large_files()
    {
        var files = new[] { F("x64a.rpf", 3 * ByteSize.GiB), F("common.rpf", 40 * ByteSize.MiB), F("settings.xml", 2000), F("GTA5.exe", 60 * ByteSize.MiB), F("empty.txt", 0) };
        var plan = IndexWarmup.PlanFor(files, ByteSize.GiB, new HashSet<string> { "GTA5.exe" });
        Assert.Equal(1, plan.SmallFiles);
        Assert.Equal(2, plan.LargeFiles);
        Assert.Equal(0, plan.Trimmed);
        Assert.Contains(new IndexWarmup.Range("settings.xml", 0, 2000), plan.Ranges);
        Assert.Contains(new IndexWarmup.Range("x64a.rpf", 0, 2 * ByteSize.MiB), plan.Ranges);
        Assert.Contains(new IndexWarmup.Range("x64a.rpf", 3 * ByteSize.GiB - 2 * ByteSize.MiB, 2 * ByteSize.MiB), plan.Ranges);
        Assert.DoesNotContain(plan.Ranges, r => r.RelativePath == "GTA5.exe" || r.RelativePath == "empty.txt");
        Assert.Equal(plan.Ranges.Sum(r => r.Length), plan.Bytes);
    }

    [Fact]
    public void A_file_just_above_the_small_limit_gets_no_overlapping_ranges()
    {
        var size = IndexWarmup.SmallFileBytes + 1;
        var plan = IndexWarmup.PlanFor([F("a.pak", size)], ByteSize.GiB);
        var head = plan.Ranges.Single(r => r.Offset == 0);
        var tail = plan.Ranges.Single(r => r.Offset > 0);
        Assert.True(tail.Offset >= head.Offset + head.Length);
        Assert.Equal(size, tail.Offset + tail.Length);
    }

    [Fact]
    public void Budget_prefers_small_files_then_heads_and_reports_what_was_left_out()
    {
        var files = Enumerable.Range(0, 10).Select(i => F($"big{i}.rpf", ByteSize.GiB)).Append(F("small.dat", ByteSize.MiB)).ToList();
        var plan = IndexWarmup.PlanFor(files, 12 * ByteSize.MiB);
        Assert.Contains(plan.Ranges, r => r.RelativePath == "small.dat");
        Assert.Equal(5, plan.Ranges.Count(r => r.Offset == 0 && r.RelativePath.StartsWith("big")));
        Assert.DoesNotContain(plan.Ranges, r => r.Offset > 0);
        Assert.True(plan.Bytes <= 12 * ByteSize.MiB);
        Assert.Equal(21 - plan.Ranges.Count, plan.Trimmed);
    }
}
