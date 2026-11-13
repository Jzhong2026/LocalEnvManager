using System;
using System.IO;
using LocalEnvManager.Services;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class UiSettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public UiSettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "ui-settings.json");
    }

    [Fact]
    public void MissingFileReturnsDefaults()
    {
        var store = new JsonUiSettingsStore(_path);
        Assert.Empty(store.Load().ExecuteColumnWidths);
    }

    [Fact]
    public void SaveThenLoadRoundTripsWidths()
    {
        var store = new JsonUiSettingsStore(_path);
        store.Save(new UiSettings { ExecuteColumnWidths = { ["branch"] = 320, ["mode"] = 90 } });
        var loaded = store.Load();
        Assert.Equal(320, loaded.ExecuteColumnWidths["branch"]);
        Assert.Equal(90, loaded.ExecuteColumnWidths["mode"]);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new JsonUiSettingsStore(_path);
        Assert.Empty(store.Load().ExecuteColumnWidths);
    }

    [Fact]
    public void NullColumnWidthsNormalizesToEmpty()
    {
        File.WriteAllText(_path, """{"ExecuteColumnWidths":null}""");
        var store = new JsonUiSettingsStore(_path);
        var settings = store.Load();
        Assert.NotNull(settings.ExecuteColumnWidths);
        Assert.Empty(settings.ExecuteColumnWidths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}