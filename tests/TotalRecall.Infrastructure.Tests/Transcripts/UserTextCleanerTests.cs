using System.Diagnostics;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class UserTextCleanerTests
{
    [Theory]
    [InlineData("system-reminder")]
    [InlineData("task-notification")]
    [InlineData("local-command-stdout")]
    [InlineData("local-command-stderr")]
    [InlineData("local-command-caveat")]
    [InlineData("command-message")]
    [InlineData("bash-stdout")]
    [InlineData("bash-stderr")]
    [InlineData("EXTREMELY_IMPORTANT")]
    [InlineData("ide_selection")]
    [InlineData("ide_opened_file")]
    [InlineData("ide_diagnostics")]
    [InlineData("ide_some_future_thing")]
    public void DropsEachListedBlockWithTags(string tag)
    {
        var c = UserTextCleaner.Clean("before <" + tag + ">zqxinside\nmore</" + tag + "> after");
        Assert.Equal("before  after", c.Text);
        Assert.Empty(c.ShellInputs);

        var only = UserTextCleaner.Clean("<" + tag + ">zqxinside</" + tag + ">");
        Assert.Null(only.Text);
    }

    [Theory]
    [InlineData("system-reminder")]
    [InlineData("ide_selection")]
    [InlineData("EXTREMELY_IMPORTANT")]
    public void UnclosedBlockAtTextOrLineStart_DroppedToEnd(string tag)
    {
        Assert.Equal("keep", UserTextCleaner.Clean("keep\n<" + tag + ">zqxrest of it\nand more").Text);
        Assert.Null(UserTextCleaner.Clean("<" + tag + ">zqxrest of it\nand more").Text);
        Assert.Equal("keep", UserTextCleaner.Clean("keep\r\n<" + tag + ">zqxrest").Text);
    }

    [Fact]
    public void UnclosedBlockMidLine_IsKept()
    {
        var c = UserTextCleaner.Clean("see the <system-reminder> tag and more\nsecond line");
        Assert.Equal("see the <system-reminder> tag and more\nsecond line", c.Text);
    }

    [Fact]
    public void ClosedBlockMidLine_IsDropped_AndTextAfterKept()
    {
        Assert.Equal("a  c", UserTextCleaner.Clean("a <system-reminder>b</system-reminder> c").Text);
    }

    [Fact]
    public void CloserBeforeOpener_DoesNotCloseTheBlock()
    {
        // The closing tag sits before the opening tag, so the opener (mid-line) has none and the text is kept.
        Assert.Equal("</system-reminder> x <system-reminder> y", UserTextCleaner.Clean("</system-reminder> x <system-reminder> y").Text);
    }

    [Fact]
    public void BlocksAreNotNested_FirstCloserEndsTheBlock()
    {
        Assert.Equal("tail", UserTextCleaner.Clean("<system-reminder>a <system-reminder>b</system-reminder>tail").Text);
    }

    [Fact]
    public void DifferentBlocksBackToBack_AreAllDropped()
    {
        var c = UserTextCleaner.Clean("<local-command-caveat>c</local-command-caveat><local-command-stdout>o</local-command-stdout>words");
        Assert.Equal("words", c.Text);
    }

    [Fact]
    public void SlashCommand_BecomesPlainText()
    {
        Assert.Equal("/develop 148", UserTextCleaner.Clean("<command-name>/develop</command-name><command-args>148</command-args>").Text);
        Assert.Equal("/develop", UserTextCleaner.Clean("<command-name>/develop</command-name><command-args></command-args>").Text);
        Assert.Equal("/develop", UserTextCleaner.Clean("<command-name>/develop</command-name>").Text);
    }

    [Fact]
    public void SlashCommand_RealisticLayout_DropsCommandMessage()
    {
        var text = "<command-message>develop is running</command-message>\n<command-name>/develop</command-name>\n<command-args>148 now</command-args>";
        Assert.Equal("/develop\n148 now", UserTextCleaner.Clean(text).Text);
    }

    [Fact]
    public void SlashCommand_UnclosedCommandName_IsPlainText()
    {
        Assert.Equal("<command-name>/x", UserTextCleaner.Clean("<command-name>/x").Text);
    }

    [Fact]
    public void BashInput_BecomesShellInput_NotText()
    {
        var c = UserTextCleaner.Clean("<bash-input>ls -l</bash-input>");
        Assert.Null(c.Text);
        Assert.Equal(new[] { "ls -l" }, c.ShellInputs);
    }

    [Fact]
    public void BashInput_AmongTextAndSeveral()
    {
        var c = UserTextCleaner.Clean("run <bash-input>ls</bash-input> then <bash-input> pwd </bash-input> ok <bash-input></bash-input>");
        Assert.Equal("run  then  ok", c.Text);
        Assert.Equal(new[] { "ls", "pwd" }, c.ShellInputs);
    }

    [Fact]
    public void BashInput_Unclosed_IsKeptAsText()
    {
        var c = UserTextCleaner.Clean("<bash-input>ls");
        Assert.Equal("<bash-input>ls", c.Text);
        Assert.Empty(c.ShellInputs);
    }

    [Theory]
    [InlineData("[Request interrupted by user]")]
    [InlineData("[Request interrupted by user for tool use]")]
    [InlineData("  [Request interrupted by user]\n")]
    public void InterruptionNoticeOnly_IsNull(string text)
    {
        Assert.Null(UserTextCleaner.Clean(text).Text);
    }

    [Fact]
    public void InterruptionNotice_WithOtherText_IsKept()
    {
        Assert.Equal("wait [Request interrupted by user] ok", UserTextCleaner.Clean("wait [Request interrupted by user] ok").Text);
        Assert.Equal("[Request interrupted by user] and [more]", UserTextCleaner.Clean("[Request interrupted by user] and [more]").Text);
    }

    [Fact]
    public void InterruptionNoticeAroundDroppedBlock_IsNull()
    {
        Assert.Null(UserTextCleaner.Clean("<system-reminder>x</system-reminder>[Request interrupted by user]").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("<system-reminder>a</system-reminder>\n<task-notification>b</task-notification>")]
    public void OnlyDroppedBlocks_IsNull(string text)
    {
        var c = UserTextCleaner.Clean(text);
        Assert.Null(c.Text);
        Assert.Empty(c.ShellInputs);
    }

    [Fact]
    public void TextAroundBlocks_IsKeptAndTrimmed()
    {
        var c = UserTextCleaner.Clean("\n  <system-reminder>x</system-reminder>\n  Please fix it.  \n<local-command-stdout>y</local-command-stdout>\n");
        Assert.Equal("Please fix it.", c.Text);
    }

    [Fact]
    public void PlainText_IsUnchanged_AndOtherTagsAreKept()
    {
        var text = "Use <div>x</div> and a < b > c, also <ide_ and <ide_Upper> and <ide_x y>";
        Assert.Equal(text, UserTextCleaner.Clean(text).Text);
    }

    [Fact]
    public void TagsAreCaseSensitive()
    {
        Assert.Equal("<System-Reminder>x</System-Reminder>", UserTextCleaner.Clean("<System-Reminder>x</System-Reminder>").Text);
        Assert.Null(UserTextCleaner.Clean("<EXTREMELY_IMPORTANT>x</EXTREMELY_IMPORTANT>").Text);
        Assert.Equal("<extremely_important>x</extremely_important>", UserTextCleaner.Clean("<extremely_important>x</extremely_important>").Text);
    }

    [Fact]
    public void ManyUnclosedTags_FinishesQuickly()
    {
        const string tag = "<system-reminder>";
        var atStart = string.Concat(Enumerable.Repeat(tag, (1024 * 1024) / tag.Length + 1));
        var midLine = "x" + atStart;                                   // never at a line start: each one is kept
        var ide = string.Concat(Enumerable.Repeat("a<ide_selection>", 70_000));
        var mixed = string.Concat(Enumerable.Repeat("a<system-reminder><bash-input><command-name>", 25_000));

        var sw = Stopwatch.StartNew();
        Assert.Null(UserTextCleaner.Clean(atStart).Text);
        Assert.Equal(midLine, UserTextCleaner.Clean(midLine).Text);
        Assert.Equal(ide, UserTextCleaner.Clean(ide).Text);
        Assert.Equal(mixed, UserTextCleaner.Clean(mixed).Text);
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void ManyClosedBlocks_FinishQuickly()
    {
        var text = string.Concat(Enumerable.Repeat("a<system-reminder>b</system-reminder>", 30_000));
        var sw = Stopwatch.StartNew();
        Assert.Equal(new string('a', 30_000), UserTextCleaner.Clean(text).Text);
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void ManyCloserSearchesAfterOneFarCloser_FinishQuickly()
    {
        // Openers mid-line, then one closer at the very end: the first opener swallows everything, so the work stays linear.
        var text = "x" + string.Concat(Enumerable.Repeat("a<system-reminder>", 60_000)) + "</system-reminder>z";
        var sw = Stopwatch.StartNew();
        Assert.Equal("xaz", UserTextCleaner.Clean(text).Text);
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
    }
}
