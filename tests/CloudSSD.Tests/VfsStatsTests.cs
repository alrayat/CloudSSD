using System.Text.Json.Nodes;
using CloudSSD.Mount;

namespace CloudSSD.Tests;

public sealed class VfsStatsTests
{
    [Fact]
    public void Older_driver_without_file_details_retains_unknown_status()
    {
        var stats = VfsStats.Parse(JsonNode.Parse("""{"diskCache":{"openFiles":2}}""")!);
        Assert.Equal(2, stats.OpenFiles);
        Assert.Null(stats.OpenFilePaths);
    }

    [Fact]
    public void Detailed_driver_status_preserves_the_reported_files()
    {
        var stats = VfsStats.Parse(JsonNode.Parse("""{"diskCache":{"openFiles":2,"openFilePaths":["Games/demo.exe","Games/demo.dll"]}}""")!);
        Assert.Equal(new[] { "Games/demo.exe", "Games/demo.dll" }, stats.OpenFilePaths);
    }
}
