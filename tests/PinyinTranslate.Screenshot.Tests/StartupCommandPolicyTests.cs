using PinyinTranslate.App;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class StartupCommandPolicyTests
{
    [Fact]
    public void BothManagedDistributionsUseRootLauncherOnlyWithCompanionFiles()
    {
        const string program = @"C:\Apps\Translator\current\PinyinTranslate.App.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Translator\Update.exe", @"C:\Apps\Translator\PinyinTranslate.exe"
        };
        // No .portable marker: the installed variant must also use its root stub.
        Assert.Equal(@"C:\Apps\Translator\PinyinTranslate.exe", StartupCommandPolicy.FindManagedLauncher(program, true, files.Contains));
        files.Remove(@"C:\Apps\Translator\Update.exe");
        Assert.Equal(program, StartupCommandPolicy.FindManagedLauncher(program, true, files.Contains));
    }

    [Fact]
    public void AnUnmanagedDirectoryNeverChangesItsExecutableEvenWithLookalikeFiles()
    {
        const string program = @"C:\Apps\Translator\current\PinyinTranslate.App.exe";
        Assert.Equal(program, StartupCommandPolicy.FindManagedLauncher(program, false, _ => true));
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
