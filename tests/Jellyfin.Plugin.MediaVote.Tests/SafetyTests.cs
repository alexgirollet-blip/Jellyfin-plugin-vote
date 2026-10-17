using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Api;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Services;
using Jellyfin.Plugin.MediaVote.Web;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

/// <summary>Tests for the 1.0.1.0 safety fixes.</summary>
public class SafetyTests
{
    // ------------------------------------------------------------------ staged rollout / preview defaults

    [Fact]
    public void FreshInstall_AllOptionalEntryPointsOff_PreviewOn()
    {
        var c = new PluginConfiguration();
        Assert.False(c.EnableBanner);
        Assert.False(c.EnableCustomTab);
        Assert.False(c.EnablePluginPage);
        Assert.False(c.EnableHomeSection);
        Assert.True(c.EnableStandalonePage);
        Assert.True(c.AdminPreviewOnly);
        Assert.Equal(ArrAction.Unmonitor, c.RadarrAction);
        Assert.Equal(SeerrAction.Nothing, c.JellyseerrAction);
    }

    [Fact]
    public void UpgradeFrom100_SwitchesEverythingRiskyOffOnce()
    {
        // What 1.0.0.0 saved: every entry point on, no ConfigVersion, no preview setting.
        var c = new PluginConfiguration { ConfigVersion = 0, EnableBanner = true, EnableCustomTab = true, EnablePluginPage = true, EnableHomeSection = true, AdminPreviewOnly = false };
        Assert.True(c.Migrate());
        Assert.False(c.EnableBanner || c.EnableCustomTab || c.EnablePluginPage || c.EnableHomeSection);
        Assert.True(c.AdminPreviewOnly);
        Assert.Equal(PluginConfiguration.CurrentConfigVersion, c.ConfigVersion);

        // Later choices by the admin are kept.
        c.EnableBanner = true;
        c.AdminPreviewOnly = false;
        Assert.False(c.Migrate());
        Assert.True(c.EnableBanner);
        Assert.False(c.AdminPreviewOnly);
    }

    [Fact]
    public void Normalize_AllowsEveryEntryPointOff()
    {
        var c = new PluginConfiguration { EnableBanner = false, EnableCustomTab = false, EnablePluginPage = false, EnableStandalonePage = false };
        c.Normalize();
        Assert.False(c.EnableBanner);
    }

    // ------------------------------------------------------------------ preview / kill switch rules

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void HiddenRules(bool kill, bool preview, bool admin, bool hidden)
        => Assert.Equal(hidden, AccessRules.IsHiddenFor(kill, preview, admin));

    [Fact]
    public void BlockReasons()
    {
        Assert.Equal("disabled", AccessRules.BlockReason(true, false, true)!.Value.Code);
        Assert.Equal("preview", AccessRules.BlockReason(false, true, false)!.Value.Code);
        Assert.Null(AccessRules.BlockReason(false, true, true));
        Assert.Null(AccessRules.BlockReason(false, false, false));
    }

    [Fact]
    public void SidebarPage_HiddenFromEveryoneInPreview()
    {
        Assert.False(AccessRules.ShowSidebarPage(true, true, false));
        Assert.False(AccessRules.ShowSidebarPage(true, false, true));
        Assert.False(AccessRules.ShowSidebarPage(false, false, false));
        Assert.True(AccessRules.ShowSidebarPage(true, false, false));
    }

    [Fact]
    public void KillSwitch_FileDetection()
    {
        using var dir = new TempDir();
        Assert.False(KillSwitch.IsActiveIn(dir.Path));
        File.WriteAllText(Path.Combine(dir.Path, KillSwitch.FileName), string.Empty);
        Assert.True(KillSwitch.IsActiveIn(dir.Path));
        Assert.False(KillSwitch.IsActiveIn(Path.Combine(dir.Path, "does-not-exist")));
        Assert.False(KillSwitch.IsActiveIn(null));
        Assert.Equal("DISABLE", KillSwitch.FileName);
    }

    // ------------------------------------------------------------------ transformation callback

    private static readonly string Tag = WebInjectionService.BuildTag("/jf", "1.0.1.0");

    public static IEnumerable<object[]> Pages() => new[]
    {
        new object[] { "<html><head></head><body><div id=\"reactRoot\"></div></body></html>" },
        new object[] { "<!DOCTYPE html><html><body>a</body><!-- </body> --></html>" },
        new object[] { "<html><BODY>upper case</BODY></html>" },
        new object[] { "<html>no closing body tag" },
        new object[] { "<html><body>ünïcödé ✓ 🙂</body></html>" },
        new object[] { "x" },
        new object[] { "<html><body><script data-mediavote-script=\"1\" src=\"/MediaVote/client.js\"></script></body></html>" }
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void SafeInsert_NeverShorter_KeepsOriginal_AtMostOneTag(string html)
    {
        var result = WebInjectionService.SafeInsert(html, Tag);
        Assert.True(result.Length >= html.Length);
        Assert.Equal(html, result.Replace(Tag, string.Empty, StringComparison.Ordinal));
        Assert.True(CountMarker(result) <= Math.Max(1, CountMarker(html)));
        Assert.Equal(result, WebInjectionService.SafeInsert(result, Tag)); // idempotent
    }

    [Fact]
    public void SafeInsert_PutsTagBeforeLastBodyEnd()
    {
        const string html = "<html><body><div>x</div></body></html>";
        var result = WebInjectionService.SafeInsert(html, Tag);
        Assert.Equal("<html><body><div>x</div>" + Tag + "</body></html>", result);
    }

    [Fact]
    public void Callback_NullOrEmptyPayload_ReturnsInputUnchanged()
    {
        Assert.Null(WebInjectionService.TransformIndexHtml(null!));
        Assert.Null(WebInjectionService.TransformIndexHtml(new TransformPayload { Contents = null }));
        Assert.Equal(string.Empty, WebInjectionService.TransformIndexHtml(new TransformPayload { Contents = string.Empty }));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Callback_NeverReturnsLessThanItReceived(string html)
    {
        var result = WebInjectionService.TransformIndexHtml(new TransformPayload { Contents = html });
        Assert.NotNull(result);
        Assert.True(result!.Length >= html.Length);
        Assert.Contains(html.Replace(Tag, string.Empty, StringComparison.Ordinal), result.Replace(Tag, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Callback_PayloadDeserialisesLikeFileTransformation()
    {
        // File Transformation 3.0.0.0: new JObject { { "contents", text } }.ToObject(parameterType)
        var obj = new JObject { { "contents", "<html><body></body></html>" } };
        var payload = (TransformPayload)obj.ToObject(typeof(TransformPayload))!;
        Assert.Equal("<html><body></body></html>", payload.Contents);

        // And with System.Text.Json the same property name applies.
        var stj = JsonSerializer.Deserialize<TransformPayload>("{\"contents\":\"abc\"}");
        Assert.Equal("abc", stj!.Contents);
    }

    [Fact]
    public void CallbackSelfTest_Passes()
        => Assert.Null(WebInjectionService.SelfTest(Tag));

    [Fact]
    public void ReflectionBridge_BuildsJObjectFromTargetParameterType()
    {
        FakePluginInterface.Received = null;
        var error = ReflectionBridge.InvokeWithJObject(typeof(FakePluginInterface).Assembly, typeof(FakePluginInterface).FullName!, nameof(FakePluginInterface.Register), new { id = "x", fileNamePattern = "index.html" });
        Assert.Null(error);
        Assert.Equal("index.html", FakePluginInterface.Received!["fileNamePattern"]!.ToString());
        Assert.NotNull(ReflectionBridge.InvokeWithJObject(typeof(FakePluginInterface).Assembly, typeof(FakePluginInterface).FullName!, nameof(FakePluginInterface.Throws), new { }));
        Assert.Null(ReflectionBridge.InvokeStatic(typeof(FakePluginInterface).Assembly, typeof(FakePluginInterface).FullName!, nameof(FakePluginInterface.Remove), Guid.NewGuid()));
    }

    private static int CountMarker(string s)
    {
        int n = 0;
        for (int i = s.IndexOf(WebInjectionService.Marker, StringComparison.Ordinal); i >= 0; i = s.IndexOf(WebInjectionService.Marker, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    // ------------------------------------------------------------------ self-check and readable errors

    [Fact]
    public void SelfCheck_OneBrokenApi_DoesNotStopTheOthers()
    {
        var results = SelfCheckRunner.Run(new (string, Func<string>)[]
        {
            ("Users", () => throw new MissingMethodException("Method not found: 'IEnumerable`1<User> IUserManager.get_Users()'.")),
            ("Libraries", () => "OK, 4 found"),
            ("Play statistics", () => throw new InvalidOperationException("database locked"))
        });
        Assert.Equal(3, results.Count);
        Assert.False(results[0].Ok);
        Assert.Contains("does not match the Jellyfin version", results[0].Detail, StringComparison.Ordinal);
        Assert.Contains("get_Users", results[0].Detail, StringComparison.Ordinal);
        Assert.True(results[1].Ok);
        Assert.Equal("OK, 4 found", results[1].Detail);
        Assert.False(results[2].Ok);
        Assert.Equal("database locked", results[2].Detail);
    }

    [Fact]
    public void JellyfinCall_ExplainsWhatFailed()
    {
        var ex = Assert.Throws<JellyfinApiException>(() => JellyfinCall.Run<int>("list users", () => throw new MissingMethodException("get_Users")));
        Assert.StartsWith("Jellyfin could not list users:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ErrorText.BuiltForJellyfin, ex.Message, StringComparison.Ordinal);
        Assert.Equal(5, JellyfinCall.Run("x", () => 5));
    }

    [Fact]
    public void ErrorFilter_ReturnsReadableJson500_WithoutStackTrace()
    {
        var result = MediaVoteErrorFilterAttribute.Build(new JellyfinApiException("Jellyfin could not list users: boom", new InvalidOperationException("boom")));
        Assert.Equal(500, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Content!);
        Assert.Equal("jellyfin_api_error", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("could not list users", doc.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", result.Content, StringComparison.Ordinal);

        var mismatch = MediaVoteErrorFilterAttribute.Build(new MissingMethodException("Method not found"));
        Assert.Contains("jellyfin_version_mismatch", mismatch.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltForJellyfin_MatchesBuildYamlTargetAbi()
    {
        var root = FindRepoRoot();
        var yaml = File.ReadAllText(Path.Combine(root, "build.yaml"));
        Assert.Contains("targetAbi: \"" + ErrorText.BuiltForJellyfin + ".0\"", yaml, StringComparison.Ordinal);
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Jellyfin.Plugin.MediaVote", "Jellyfin.Plugin.MediaVote.csproj"));
        Assert.Contains("<JellyfinPackageVersion>" + ErrorText.BuiltForJellyfin + "</JellyfinPackageVersion>", csproj, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Custom Tabs (read-only)

    private static readonly string V100Tab = "<div data-mediavote-mount=\"tab\" class=\"mediavote-tab-host\"></div><script>(function(){if(document.getElementById('mediavote-loader'))return;})();</script>";

    [Fact]
    public void CustomTabs_DetectsThe100Leftover()
    {
        var findings = CustomTabsInspector.Inspect(new[] { new CustomTabInfo(0, "Requests", "<iframe src=\"x\"></iframe>"), new CustomTabInfo(1, "Vote", V100Tab) }, new Version(0, 2, 10, 0));
        var leftover = Assert.Single(findings, f => f.Kind == "LeftoverV100");
        Assert.Equal(1, leftover.Index);
        Assert.Contains("Delete this tab", leftover.Message, StringComparison.Ordinal);
        Assert.Contains(findings, f => f.Kind == "BreaksHomePage" && f.Index == 1);
        Assert.True(CustomTabsInspector.NeedsAction(findings));
    }

    [Fact]
    public void CustomTabs_SnippetIsSafeAndRecognised()
    {
        Assert.True(CustomTabsInspector.IsSafeForChunkInjection(CustomTabsInspector.Snippet));
        Assert.DoesNotContain("<script", CustomTabsInspector.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-mediavote-mount", CustomTabsInspector.Snippet, StringComparison.Ordinal);
        var findings = CustomTabsInspector.Inspect(new[] { new CustomTabInfo(0, "Vote", CustomTabsInspector.Snippet) }, new Version(0, 2, 10, 0));
        Assert.Equal("MediaVoteTab", Assert.Single(findings).Kind);
        Assert.False(CustomTabsInspector.NeedsAction(findings));
    }

    [Fact]
    public void CustomTabs_ApostropheWarnsOnlyForChunkInjectingVersions()
    {
        var tabs = new[] { new CustomTabInfo(0, "Info", "<p>It doesn't matter</p>") };
        Assert.Contains(CustomTabsInspector.Inspect(tabs, new Version(0, 2, 10, 0)), f => f.Kind == "BreaksHomePage");
        Assert.Empty(CustomTabsInspector.Inspect(tabs, new Version(0, 3, 0, 0)));
        Assert.False(CustomTabsInspector.IsSafeForChunkInjection("a$1b"));
        Assert.False(CustomTabsInspector.IsSafeForChunkInjection("a\\b"));
        Assert.False(CustomTabsInspector.IsSafeForChunkInjection("line\nbreak"));
    }

    [Fact]
    public void CustomTabs_ReadTabs_IsReadOnly()
    {
        var config = new FakeCustomTabsConfig { Tabs = new[] { new FakeTab { Title = "A", ContentHtml = "<b>a</b>" }, new FakeTab { Title = "Vote", ContentHtml = V100Tab } } };
        var before = JsonSerializer.Serialize(config);
        var tabs = EntryPointRegistrar.ReadTabs(config)!;
        Assert.Equal(2, tabs.Count);
        Assert.Equal("Vote", tabs[1].Title);
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Null(EntryPointRegistrar.ReadTabs(new object()));
        Assert.Null(EntryPointRegistrar.ReadTabs(null));
    }

    // ------------------------------------------------------------------ no writes to other plugins

    [Fact]
    public void Plugin_NeverWritesOtherPluginsConfiguration()
    {
        var path = typeof(Plugin).Assembly.Location;
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var referenced = new List<string>();
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            referenced.Add(md.GetString(tr.Namespace) + "." + md.GetString(tr.Name) + "::" + md.GetString(mr.Name));
        }

        // Writing another plugin's settings would need one of these: none may be referenced.
        Assert.DoesNotContain("MediaBrowser.Common.Plugins.IHasPluginConfiguration::UpdateConfiguration", referenced);
        Assert.DoesNotContain("MediaBrowser.Common.Plugins.IPlugin::UpdateConfiguration", referenced);
        Assert.DoesNotContain("System.Reflection.PropertyInfo::SetValue", referenced);
        Assert.DoesNotContain("System.Reflection.FieldInfo::SetValue", referenced);
        Assert.DoesNotContain("System.Array::SetValue", referenced);
        Assert.DoesNotContain("System.IO.File::WriteAllText", referenced);
        Assert.DoesNotContain("System.IO.File::WriteAllBytes", referenced);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

public static class FakePluginInterface
{
    public static JObject? Received { get; set; }

    public static void Register(JObject payload) => Received = payload;

    public static void Throws(JObject payload) => throw new InvalidOperationException("boom");

    public static void Remove(Guid id)
    {
    }
}

public sealed class FakeCustomTabsConfig
{
    public FakeTab[] Tabs { get; set; } = Array.Empty<FakeTab>();
}

public sealed class FakeTab
{
    public string Title { get; set; } = string.Empty;

    public string ContentHtml { get; set; } = string.Empty;
}
