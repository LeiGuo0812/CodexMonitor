using CodexQuotaMonitor.App;
using CodexQuotaMonitor.Core;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class RegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cqm-tests-" + Guid.NewGuid().ToString("N"));

    private string Create(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture");
        return path;
    }

    [Fact]
    public void ExplorerWithoutCliInPathFindsDesktopBundledCli()
    {
        var cli = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "version-a", "codex.exe"));
        var found = CodexExecutableLocator.FindCandidates(null, "", Path.Combine(_root, "profile"), Path.Combine(_root, "local"));
        Assert.Equal(cli, Assert.Single(found));
    }

    [Fact]
    public void DesktopUpdateIsDiscoveredWithoutRememberingOldVersionFolder()
    {
        var oldCli = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "old", "codex.exe"));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(oldCli)!, DateTime.UtcNow.AddDays(-1));
        var newCli = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "new", "codex.exe"));
        var found = CodexExecutableLocator.FindCandidates(null, "", _root, Path.Combine(_root, "local"));
        Assert.Equal(newCli, found[0]);
        Assert.Contains(oldCli, found);
    }

    [Fact]
    public void VersionedDesktopCliPrecedesStaleUnversionedCopy()
    {
        var oldCli = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "codex.exe"));
        var currentCli = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "current", "codex.exe"));
        var found = CodexExecutableLocator.FindCandidates(null, "", _root, Path.Combine(_root, "local"));
        Assert.Equal(new[] { currentCli, oldCli }, found);
    }

    [Fact]
    public void ExplicitMissingPathDoesNotSwitchInstallations()
    {
        Create(Path.Combine("local", "OpenAI", "Codex", "bin", "version-a", "codex.exe"));
        Assert.Empty(CodexExecutableLocator.FindCandidates(Path.Combine(_root, "missing.exe"), "", _root, Path.Combine(_root, "local")));
    }

    [Fact]
    public void DiscoveryKeepsFallbackCandidatesAfterAnUnverifiedPathEntry()
    {
        var first = Create(Path.Combine("path", "codex.exe"));
        var bundled = Create(Path.Combine("local", "OpenAI", "Codex", "bin", "version-a", "codex.exe"));
        var found = CodexExecutableLocator.FindCandidates(null, Path.GetDirectoryName(first)!, _root, Path.Combine(_root, "local"));
        Assert.Equal(new[] { first, bundled }, found);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void ShellForegroundDoesNotHideWidget(string windowClass) =>
        Assert.False(WidgetVisibilityPolicy.ShouldHide(windowClass, true, false, false, false, true));

    [Fact]
    public void GenuineFullscreenHidesWidget() =>
        Assert.True(WidgetVisibilityPolicy.ShouldHide("GameWindow", true, false, false, false, true));

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    public void OrdinaryWindowsDoNotHideWidget(bool visible, bool minimized, bool ownProcess, bool maximizedWithCaption, bool coversMonitor) =>
        Assert.False(WidgetVisibilityPolicy.ShouldHide("AppWindow", visible, minimized, ownProcess, maximizedWithCaption, coversMonitor));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
