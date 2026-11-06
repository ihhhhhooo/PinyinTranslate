using PinyinTranslate.App;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class StartupCommandPolicyTests
{
    [Fact]
    public void PortableStartupUsesRootLauncherOnlyWhenBothCompanionFilesExist()
    {
        const string program = @"C:\Apps\Translator\current\PinyinTranslate.App.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Translator\.portable", @"C:\Apps\Translator\PinyinTranslate.exe"
        };
        Assert.Equal(@"C:\Apps\Translator\PinyinTranslate.exe", StartupCommandPolicy.FindPortableLauncher(program, files.Contains));
        files.Remove(@"C:\Apps\Translator\.portable");
        Assert.Equal(program, StartupCommandPolicy.FindPortableLauncher(program, files.Contains));
    }

    [Theory]
    [InlineData(@"C:\Old\PinyinTranslate.App.exe")]
    [InlineData(@"C:\Old\PinyinTranslate.exe")]
    public void ExistingOptInCanFollowMove(string previous)
    {
        Assert.True(StartupCommandPolicy.ShouldRelocate('"' + previous + '"', @"""C:\New\PinyinTranslate.exe"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\Other\\unrelated.exe\"")]
    [InlineData("\"C:\\Old\\PinyinTranslate.App.exe\" --custom")]
    [InlineData("\"C:\\Old\\PinyinTranslate.App.exe\" \"extra\"")]
    [InlineData("\"relative\\PinyinTranslate.App.exe\"")]
    public void DisabledOrUnrecognizedCommandsAreNeverChanged(string? previous)
    {
        Assert.False(StartupCommandPolicy.ShouldRelocate(previous, @"""C:\New\PinyinTranslate.exe"""));
    }

    [Fact]
    public void MatchingStartupDoesNotNeedAWrite()
    {
        Assert.False(StartupCommandPolicy.ShouldRelocate(@"""c:\new\pinyintranslate.exe""", @"""C:\New\PinyinTranslate.exe"""));
    }
}
