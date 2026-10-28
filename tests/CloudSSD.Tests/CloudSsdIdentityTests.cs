using CloudSSD.Core;

namespace CloudSSD.Tests;

public sealed class CloudSsdIdentityTests
{
    [Fact]
    public void IdentityUsesDedicatedProfileDatabaseAndCachePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudssd-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            Assert.Equal(Path.Combine(root, "CloudSSD"), AppPaths.ResolveDefaultDataRoot(root));
            Assert.Equal(Path.Combine(root, "cloudssd.db"), paths.DatabaseFile);
            Assert.Equal(Path.Combine(root, "cache"), paths.DefaultCacheRoot);
            Assert.True(Directory.Exists(paths.LogDirectory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
