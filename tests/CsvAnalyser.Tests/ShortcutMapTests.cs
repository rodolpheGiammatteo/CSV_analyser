using CsvAnalyser.Core.Shortcuts;

namespace CsvAnalyser.Tests;

public class ShortcutMapTests
{
    private static readonly (string Id, string Default)[] Actions =
    [
        ("file.open", "Ctrl+O"),
        ("measure.x", "Ctrl+M"),
        ("curve.computedFrom", ""),
    ];

    [Fact]
    public void NoOverride_GivesTheDefaults()
    {
        var map = ShortcutMap.Effective(Actions, null);
        Assert.Equal("Ctrl+O", map["file.open"]);
        Assert.Equal("", map["curve.computedFrom"]);
    }

    [Fact]
    public void Override_ReplacesOrRemovesADefault()
    {
        var map = ShortcutMap.Effective(Actions, new Dictionary<string, string> { ["curve.computedFrom"] = "Ctrl+F", ["measure.x"] = "" });
        Assert.Equal("Ctrl+F", map["curve.computedFrom"]);
        Assert.Equal("", map["measure.x"]);
        Assert.Equal("Ctrl+O", map["file.open"]);
    }

    [Fact]
    public void SameGestureTwice_TheUsersChoiceWins()
    {
        var map = ShortcutMap.Effective(Actions, new Dictionary<string, string> { ["curve.computedFrom"] = "ctrl+o" });
        Assert.Equal("ctrl+o", map["curve.computedFrom"]);
        Assert.Equal("", map["file.open"]);
    }

    [Fact]
    public void Owner_FindsTheOtherActionIgnoringCase()
    {
        var map = ShortcutMap.Effective(Actions, null);
        Assert.Equal("measure.x", ShortcutMap.Owner(map, "ctrl+m"));
        Assert.Null(ShortcutMap.Owner(map, "Ctrl+M", exceptId: "measure.x"));
        Assert.Null(ShortcutMap.Owner(map, ""));
    }

    [Fact]
    public void Overrides_KeepOnlyTheDifferences()
    {
        var map = ShortcutMap.Effective(Actions, null);
        map["curve.computedFrom"] = "Ctrl+F";
        map["file.open"] = "";
        var kept = ShortcutMap.Overrides(map, Actions);
        Assert.Equal(2, kept.Count);
        Assert.Equal("Ctrl+F", kept["curve.computedFrom"]);
        Assert.Equal("", kept["file.open"]);
    }
}
