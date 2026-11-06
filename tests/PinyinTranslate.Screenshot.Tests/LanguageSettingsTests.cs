using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PinyinTranslate.App;
using PinyinTranslate.Core;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Screenshot.Tests;

// No DesktopServices constructor, dispatcher, native input, actual settings or network.
public sealed class LanguageSettingsTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void OldSettingsDefaultToEnglishInputChineseScreenshotsAndChineseEnglishSelection()
    {
        const string json = "{\"ImeHotkey\":\"Ctrl+Alt+F8\",\"SelectionHotkey\":\"Ctrl+Alt+F9\",\"ScreenshotHotkey\":\"Ctrl+Alt+F10\",\"Theme\":\"gold\"}";
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!.NormalizeLanguages();
        settings.ValidateLanguages();
        Assert.Equal("en", settings.ImeTargetLanguage);
        Assert.Equal("zh-Hans", settings.ScreenshotTargetLanguage);
        Assert.Equal("zh-Hans", settings.SelectionLanguageA);
        Assert.Equal("en", settings.SelectionLanguageB);
        Assert.Equal("gold", settings.Theme);
        Assert.Equal("Ctrl+Alt+F8", settings.ImeHotkey);
        Assert.True(settings.SelectionAutoCopy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoCopyPreferencePersistsWithoutChangingLanguagesShortcutsOrNativeServices(bool enabled)
    {
        using var fixture = new Fixture();
        var settings = fixture.Services.Settings with { SelectionAutoCopy = enabled };
        Assert.Null(await fixture.Services.ApplySettingsAsync(settings));
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(fixture.SettingsPath))!.NormalizeLanguages();
        Assert.Equal(settings, saved);
        Assert.Equal(enabled, fixture.Services.Settings.SelectionAutoCopy);
        Assert.Null(fixture.Get("_hotkeys"));
        Assert.Equal(0, fixture.Monitor.ResetCount);
        Assert.Null(fixture.Runtime.Get("_contexts"));
        Assert.Null(fixture.Runtime.Get("_keyboard"));
    }

    [Fact]
    public async Task AFailedPreferenceSaveKeepsThePreviousAutoCopySetting()
    {
        using var fixture = new Fixture();
        fixture.Set("_settingsPath", fixture.DirectoryPath);
        Assert.NotNull(await fixture.Services.ApplySettingsAsync(fixture.Services.Settings with { SelectionAutoCopy = false }));
        Assert.True(fixture.Services.Settings.SelectionAutoCopy);
        Assert.Equal(0, fixture.Monitor.ResetCount);
    }

    [Theory]
    [InlineData(null, "unknown", "unknown", null)]
    [InlineData("unknown", "en", "en", "unknown")]
    [InlineData("en", "zh-Hans", "unknown", "ja")]
    public void InvalidSavedLanguageIdentifiersAreNormalizedWithoutChangingOtherSettings(string? input, string a, string b, string? screenshot)
    {
        var settings = new AppSettings("ime", "selection", "screenshot", "pop", input!, a, b, screenshot!).NormalizeLanguages();
        settings.ValidateLanguages();
        Assert.Equal("en", settings.ImeTargetLanguage);
        Assert.Equal("zh-Hans", settings.SelectionLanguageA);
        Assert.Equal("en", settings.SelectionLanguageB);
        Assert.Equal("pop", settings.Theme);
        Assert.Equal("ime", settings.ImeHotkey);
    }

    [Fact]
    public async Task SavingLanguagesRoundTripsAllFourChoicesWithoutRegisteringHotkeysOrLaunchingNativeServices()
    {
        using var fixture = new Fixture();
        var settings = fixture.Services.Settings with
        { ImeTargetLanguage = "ja", SelectionLanguageA = "en", SelectionLanguageB = "ko", ScreenshotTargetLanguage = "zh-Hant" };
        Assert.Null(await fixture.Services.ApplySettingsAsync(settings));
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(fixture.SettingsPath));
        Assert.Equal(settings, saved);
        Assert.Equal(settings, fixture.Services.Settings);
        Assert.Null(fixture.Get("_hotkeys"));
        Assert.Null(fixture.Runtime.Get("_contexts"));
        Assert.Null(fixture.Runtime.Get("_keyboard"));
        Assert.Equal(1, fixture.Monitor.ResetCount);
        Assert.True(fixture.Monitor.RequiredFreshSelection);
    }

    [Fact]
    public async Task InvalidPairAndFailedDiskSaveLeaveTheActiveSettingsAndRuntimeUntouched()
    {
        using var fixture = new Fixture();
        var original = fixture.Services.Settings;
        Assert.NotNull(await fixture.Services.ApplySettingsAsync(original with { SelectionLanguageB = original.SelectionLanguageA }));
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.Equal(original, fixture.Services.Settings);
        long generation = fixture.Runtime.Coordinator.State.Generation;
        fixture.Set("_settingsPath", fixture.DirectoryPath); // An existing directory cannot be replaced by a settings file.
        Assert.NotNull(await fixture.Services.ApplySettingsAsync(original with { ImeTargetLanguage = "ko", SelectionLanguageB = "ja" }));
        Assert.Equal(original, fixture.Services.Settings);
        Assert.Equal(generation, fixture.Runtime.Coordinator.State.Generation);
        Assert.Equal(0, fixture.Monitor.ResetCount);
    }

    [Fact]
    public async Task CandidateRequestsAndCachesFollowTheChosenTargetIncludingChineseScripts()
    {
        using var fixture = new Fixture();
        const string source = "属于";
        fixture.SetSettings(fixture.Services.Settings with { ImeTargetLanguage = "ja" });
        Assert.Equal("translated-1", await fixture.Candidate(source));
        Assert.Equal("translated-1", await fixture.Candidate(source));
        fixture.SetSettings(fixture.Services.Settings with { ImeTargetLanguage = "zh-Hant" });
        Assert.Equal("translated-2", await fixture.Candidate(source));
        fixture.SetSettings(fixture.Services.Settings with { ImeTargetLanguage = "zh-Hans" });
        Assert.Equal("translated-3", await fixture.Candidate(source));
        Assert.Equal(3, fixture.Bodies.Count);
        Assert.Equal(new[] { "Japanese", "Traditional Chinese", "Simplified Chinese" },
            fixture.Bodies.Select(body =>
            {
                using var document = JsonDocument.Parse(body);
                string prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                return TranslationLanguages.All.Single(language => prompt.Contains($"({language.Code})", StringComparison.Ordinal)).PromptName;
            }));
    }

    [Fact]
    public async Task AnInFlightScreenshotKeepsItsOriginalTargetWhenSettingsChange()
    {
        using var fixture = new Fixture();
        fixture.SetSettings(fixture.Services.Settings with { ScreenshotTargetLanguage = "zh-Hant" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { entered.TrySetResult(); return reply.Task; };
        Task<ScreenshotResult> request = fixture.Services.TranslateScreenshotAsync([137, 80, 78, 71, 13, 10, 26, 10], CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.SetSettings(fixture.Services.Settings with { ScreenshotTargetLanguage = "ja" });
        reply.SetResult("{\"groups\":[{\"id\":\"1\",\"source\":\"software\",\"translation\":\"軟體\",\"uncertain\":false}]}");
        var result = await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("zh-Hant", result.TargetLanguage);
        Assert.Equal("軟體", Assert.Single(result.Items).Translation);
        Assert.Equal("ja", fixture.Services.Settings.ScreenshotTargetLanguage);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TranslationCache cache = new();
        private readonly HttpClient http;
        private static readonly string TemporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PinyinTranslate-language-tests"));
        public string DirectoryPath { get; } = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
        public DesktopServices Services { get; } = (DesktopServices)RuntimeHelpers.GetUninitializedObject(typeof(DesktopServices));
        public HeadlessRuntimeFixture Runtime { get; } = new(copyOnly: true);
        public FakeMonitor Monitor { get; } = new();
        public List<string> Bodies { get; } = [];
        public Func<string, CancellationToken, Task<string>> Response { get; set; }
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Response = (_, _) => Task.FromResult("translated-" + Bodies.Count);
            http = new HttpClient(new Handler(async (request, token) =>
            {
                string body = await request.Content!.ReadAsStringAsync(token);
                Bodies.Add(body);
                string content = await Response(body, token);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }))
                };
            }));
            Set("_gate", new object()); Set("_directory", DirectoryPath); Set("_settingsPath", SettingsPath);
            Set("_cache", cache); Set("_client", new DeepSeekClient(http, new FakeLedger(), () => "fake-key"));
            Set("_ime", Runtime.Runtime); Set("_selection", Monitor);
            SetSettings(new("Ctrl+Alt+T", "Ctrl+Alt+D", "Ctrl+Alt+S"));
        }
        public Task<string> Candidate(string source) => (Task<string>)typeof(DesktopServices).GetMethod("TranslateCandidateAsync", PrivateInstance)!
            .Invoke(Services, [source, CancellationToken.None])!;
        public void SetSettings(AppSettings settings) => Set("<Settings>k__BackingField", settings);
        public void Set(string name, object? value) => typeof(DesktopServices).GetField(name, PrivateInstance)!.SetValue(Services, value);
        public object? Get(string name) => typeof(DesktopServices).GetField(name, PrivateInstance)!.GetValue(Services);
        public void Dispose()
        {
            Runtime.Dispose(); cache.Dispose(); http.Dispose();
            string resolved = Path.GetFullPath(DirectoryPath);
            if (Path.GetDirectoryName(resolved) == TemporaryRoot && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class FakeLedger : IApiRequestLedger
    {
        public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens) => Guid.NewGuid();
        public void Complete(Guid id, int inputTokens, int outputTokens) { }
        public void MarkUnknown(Guid id) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FakeMonitor : ISelectionMonitor
    {
        public int ResetCount;
        public bool RequiredFreshSelection;
        public Task<bool> Ready => Task.FromResult(true);
        public bool IsCurrent(long version) => false;
        public Task<bool> ValidateAsync(SelectionSnapshot snapshot) => Task.FromResult(false);
        public void SetEnabled(bool enabled, bool requireFreshSelection = false) { ResetCount++; RequiredFreshSelection = requireFreshSelection; }
        public void Dispose() { }
        public event Action<long>? Invalidated { add { } remove { } }
        public event Action<SelectionSnapshot>? SelectionChanged { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }
    }
}
