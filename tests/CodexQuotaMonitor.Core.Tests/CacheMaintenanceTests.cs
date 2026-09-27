using CodexQuotaMonitor.App;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class CacheMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexMonitor-CacheTests-" + Guid.NewGuid().ToString("N"));

    private string Bundle(string name, string hash, string? owner = CacheMaintenance.Owner)
    {
        var path = Path.Combine(_root, name, hash);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "CodexQuotaMonitor.dll"), "test fixture");
        if (owner is not null) File.WriteAllText(Path.Combine(path, CacheMaintenance.MarkerName), owner);
        return path;
    }

    [Fact]
    public void FindsOwnVersionsIncludingRenamedHost()
    {
        var first = Bundle("CodexQuotaMonitor", "hash1");
        var renamed = Bundle("CodexMonitor-v1.0.0-win-x64", "hash2");
        var results = CacheMaintenance.FindOwnedBundles(_root);
        Assert.Equal(2, results.Length);
        Assert.Contains(first, results);
        Assert.Contains(renamed, results);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("another-application")]
    public void DoesNotClaimUnmarkedOrForeignCache(string? owner)
    {
        Bundle("OtherApp", "hash", owner);
        Assert.Empty(CacheMaintenance.FindOwnedBundles(_root));
    }

    [Fact]
    public void RejectsWrongDepthAndMissingAssembly()
    {
        var path = Bundle("App", "hash");
        Assert.False(CacheMaintenance.IsOwnedBundle(Path.Combine(_root, "wrong"), path));
        Assert.False(CacheMaintenance.IsOwnedBundle(_root, Path.GetDirectoryName(path)!));
        File.Delete(Path.Combine(path, "CodexQuotaMonitor.dll"));
        Assert.False(CacheMaintenance.IsOwnedBundle(_root, path));
    }

    [Fact]
    public void MissingCacheIsEmpty() => Assert.Empty(CacheMaintenance.FindOwnedBundles(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
