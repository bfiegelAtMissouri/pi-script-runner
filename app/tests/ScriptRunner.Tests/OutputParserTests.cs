using System.Text;
using ScriptRunner.Core;
using Xunit;

namespace ScriptRunner.Tests;

public sealed class OutputParserTests
{
    [Fact]
    public void CompleteLinesBecomeText()
    {
        var parser = new OutputParser();
        IReadOnlyList<OutputItem> items = parser.Feed("one\ntwo\n");
        Assert.Equal(new OutputItem[] { new TextItem("one\n"), new TextItem("two\n") }, items);
        Assert.Equal("", parser.LiveText);
    }

    [Fact]
    public void PromptWithoutNewlineIsLiveText()
    {
        var parser = new OutputParser();
        Assert.Empty(parser.Feed("Enter speed: "));
        Assert.Equal("Enter speed: ", parser.LiveText);
    }

    [Fact]
    public void TagsAreRecognized()
    {
        var parser = new OutputParser();
        IReadOnlyList<OutputItem> items = parser.Feed(
            "@@runner say Hello there\n@@runner play beep.wav\n@@runner show map.png\n");
        Assert.Equal(new OutputItem[]
        {
            new SayItem("Hello there"), new PlayItem("beep.wav"), new ShowItem("map.png"),
        }, items);
    }

    [Fact]
    public void TagSplitAcrossChunksIsNotShownWhileIncomplete()
    {
        var parser = new OutputParser();
        Assert.Empty(parser.Feed("@@run"));
        Assert.Equal("", parser.LiveText);
        Assert.Empty(parser.Feed("ner say Hi"));
        Assert.Equal("", parser.LiveText);
        Assert.Equal(new OutputItem[] { new SayItem("Hi") }, parser.Feed("\n"));
    }

    [Fact]
    public void TextBeforeATagIsKept()
    {
        var parser = new OutputParser();
        Assert.Equal(new OutputItem[] { new TextItem("Speed: \n"), new SayItem("Go") },
                     parser.Feed("Speed: @@runner say Go\n"));
    }

    [Fact]
    public void UnknownTagIsShownAsText()
    {
        var parser = new OutputParser();
        Assert.Equal(new OutputItem[] { new TextItem("@@runner dance now\n") }, parser.Feed("@@runner dance now\n"));
    }

    [Fact]
    public void TerminalLineEndingsSplitAcrossChunksAreRemoved()
    {
        var parser = new OutputParser();
        Assert.Empty(parser.Feed("side 1\r"));
        Assert.Equal("side 1", parser.LiveText);
        Assert.Equal(new OutputItem[] { new TextItem("side 1\n") }, parser.Feed("\n"));
    }

    [Fact]
    public void CommitPartialKeepsPromptAndAnswerTogether()
    {
        var parser = new OutputParser();
        parser.Feed("Name? ");
        Assert.Equal(new TextItem("Name? "), parser.CommitPartial());
        Assert.Equal("", parser.LiveText);
        Assert.Null(parser.CommitPartial());
    }

    [Fact]
    public void FlushEndsTheLastLine()
    {
        var parser = new OutputParser();
        parser.Feed("no newline at end");
        Assert.Equal(new OutputItem[] { new TextItem("no newline at end\n") }, parser.Flush());
    }

    [Theory]
    [InlineData("square.py")]
    [InlineData("my file.wav")]
    public void UsableFileNamesAreAccepted(string name) => Assert.Equal(name, SshRunnerConnection.SafeFileName(name));

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("../x.py")]
    [InlineData("dir/x.py")]
    [InlineData("dir\\x.py")]
    public void UnsafeFileNamesAreRejected(string name) =>
        Assert.Throws<RunnerException>(() => SshRunnerConnection.SafeFileName(name));

    [Theory]
    [InlineData("dot.png", "script_runner_ssh/run/dot.png")]
    [InlineData("/tmp/photo.jpg", "/tmp/photo.jpg")]
    [InlineData("/tmp/pictures/photo.jpg", "/tmp/pictures/photo.jpg")]
    public void MediaPathsInTheRunFolderAndTmpAreAccepted(string name, string path) =>
        Assert.Equal(path, SshRunnerConnection.RemoteMediaPath(name));

    [Theory]
    [InlineData("/tmp/")]
    [InlineData("/tmp/../etc/passwd")]
    [InlineData("/tmpfile.jpg")]
    [InlineData("/home/pi/photo.jpg")]
    [InlineData("../photo.jpg")]
    public void MediaPathsElsewhereAreRefused(string name) =>
        Assert.Throws<RunnerException>(() => SshRunnerConnection.RemoteMediaPath(name));

    [Fact]
    public void ShellQuoteHandlesSingleQuotes() =>
        Assert.Equal("'it'\\''s.py'", SshRunnerConnection.ShellQuote("it's.py"));

    [Fact]
    public void SettingsNeverPrintTheSecret()
    {
        var settings = new ConnectionSettings(ConnectionMode.Ssh, "pi.local", 22, "pi", "not-a-real-secret");
        Assert.DoesNotContain("not-a-real-secret", settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TextFilesGetAPreviewOfTheirFirstLines()
    {
        string lines = string.Join("\n", Enumerable.Range(1, 25).Select(n => "row " + n)) + "\n";
        using var shown = ScriptRunner.App.ViewModels.ShownFile.Create("/tmp/log.csv", Encoding.UTF8.GetBytes(lines));
        Assert.False(shown.IsImage);
        Assert.Equal("log.csv", shown.FileName);
        Assert.StartsWith("row 1\nrow 2\n", shown.Preview, StringComparison.Ordinal);
        Assert.EndsWith("row 20\n…", shown.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTextFilesArePreviewedWhole()
    {
        using var shown = ScriptRunner.App.ViewModels.ShownFile.Create("data.txt", Encoding.UTF8.GetBytes("a\nb\n"));
        Assert.Equal("a\nb", shown.Preview);
    }

    [Theory]
    [InlineData("model.pt")]                       // not a type the app previews
    [InlineData("binary.csv")]                     // looks like text by name, but holds binary data
    public void OtherFilesGetACardWithoutPreview(string name)
    {
        using var shown = ScriptRunner.App.ViewModels.ShownFile.Create(name, new byte[] { 1, 0, 2, 3 });
        Assert.Null(shown.Preview);
        Assert.False(shown.IsImage);
        Assert.Equal("4 bytes, no preview", shown.Note);
    }
}
