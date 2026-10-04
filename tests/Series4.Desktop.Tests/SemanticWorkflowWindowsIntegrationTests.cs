using System.Diagnostics;
using System.Runtime.InteropServices;
using Series4.Desktop;
using SharpHook.Data;
using Xunit;

namespace Series4.Desktop.Tests;

public sealed class WindowsIntegrationFactAttribute : FactAttribute
{
    public WindowsIntegrationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SERIES4_WINDOWS_UI_INTEGRATION") != "1")
            Skip = "Set SERIES4_WINDOWS_UI_INTEGRATION=1 on an interactive Windows desktop.";
    }
}

public sealed class SemanticWorkflowWindowsIntegrationTests
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);

    [WindowsIntegrationFact]
    public void RecordedNoteTask_ProducesVisibleTextAndSavedStateInARealWindow()
    {
        RunWithFixture("note", video =>
        {
            var start = Frame("start", .3, Element("Button", "새 메모", "new-note"));
            var empty = Frame("empty", .8, Element("Edit", "메모 내용", "note-editor"));
            var first = Frame("first", .95, Element("Edit", "메모 내용", "note-editor", "회"));
            var second = Frame("second", 1.1, Element("Edit", "메모 내용", "note-editor", "회의"));
            var typed = Frame("typed", 1.25, Element("Edit", "메모 내용", "note-editor", "회의록"));
            var final = Frame("final", 1.6,
                Element("Edit", "메모 내용", "note-editor", "회의록"),
                Element("Text", "저장됨", "note-status"));
            var editor = Selector("Edit", "메모 내용", "note-editor");
            var recorded = new[]
            {
                RecordedClick(1, .5, start, Selector("Button", "새 메모", "new-note")),
                RecordedKey(2, .85, KeyCode.VcH, empty, first, editor),
                RecordedKey(3, 1.0, KeyCode.VcO, first, second, editor),
                RecordedKey(4, 1.15, KeyCode.VcI, second, typed, editor),
            };
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "새 메모를 만들고 회의록이라고 입력해 줘", video, 2, recorded, final);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.Equal("SUCCESS", result.Status);
            Assert.Contains(visible.Elements, element => element.AutomationId == "note-editor" && element.Value == "회의록");
            Assert.Contains(visible.Elements, element => element.Name == "저장됨");
        });
    }

    [WindowsIntegrationFact]
    public void RecordedSettingsTask_ProducesTheVisibleEnabledStateInARealWindow()
    {
        RunWithFixture("settings", video =>
        {
            var light = Frame("light", .4,
                Element("CheckBox", "어두운 모드", "dark-mode", "Off"));
            var dark = Frame("dark", .9,
                Element("CheckBox", "어두운 모드", "dark-mode", "On"),
                Element("Text", "어두운 모드 사용 중", "mode-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "설정에서 어두운 모드를 켜 줘", video, 1.5,
                [RecordedClick(1, .6, light, Selector("CheckBox", "어두운 모드", "dark-mode"))],
                dark);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.Equal("SUCCESS", result.Status);
            Assert.Contains(visible.Elements, element => element.AutomationId == "dark-mode" && element.Value == "On");
            Assert.Contains(visible.Elements, element => element.Name == "어두운 모드 사용 중");
        });
    }

    private static void RunWithFixture(string mode, Action<string> test)
    {
        var executable = FixtureExecutable();
        Assert.True(File.Exists(executable), $"Windows UX fixture was not built: {executable}");
        using var process = Process.Start(new ProcessStartInfo(executable, mode) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Windows UX fixture를 시작하지 못했습니다.");
        var video = Path.Combine(Path.GetTempPath(), $"semantic-workflow-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]);
        try
        {
            process.WaitForInputIdle(10000);
            for (var attempt = 0; attempt < 40 && process.MainWindowHandle == IntPtr.Zero; attempt++)
            {
                Thread.Sleep(100);
                process.Refresh();
            }
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
            BringWindowToTop(process.MainWindowHandle);
            SetForegroundWindow(process.MainWindowHandle);
            Thread.Sleep(500);
            test(video);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            }
            File.Delete(video);
        }
    }

    private static string FixtureExecutable()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Name ?? "Release";
        var testsDirectory = baseDirectory;
        for (var index = 0; index < 5; index++)
            testsDirectory = testsDirectory.Parent ?? throw new DirectoryNotFoundException(AppContext.BaseDirectory);
        return Path.Combine(testsDirectory.FullName, "SemanticWorkflowFixture", "bin", "x64", configuration,
            "net10.0-windows", "SemanticWorkflowFixture.exe");
    }

    private static SemanticDemonstrationFrame Frame(string id, double offset, params SemanticElementEvidence[] elements) =>
        new(id, offset, "SemanticWorkflowFixture", "Semantic Workflow UX Fixture", string.Empty, elements);

    private static SemanticElementEvidence Element(string role, string name, string automationId, string value = "") =>
        new(role, name, automationId, value);

    private static SemanticTargetSelector Selector(string role, string name, string automationId) =>
        new([role], name, automationId);

    private static RecordedEvent RecordedClick(
        long sequence,
        double offset,
        SemanticDemonstrationFrame before,
        SemanticTargetSelector target) => new()
        {
            Offset = TimeSpan.FromSeconds(offset),
            Category = "마우스",
            Message = "왼쪽 클릭",
            ActionKind = MacroActionKind.MouseLeftClick,
            Sequence = sequence,
            CaptureWidth = 1920,
            CaptureHeight = 1080,
            SemanticBefore = before,
            SemanticTarget = target,
        };

    private static RecordedEvent RecordedKey(
        long sequence,
        double offset,
        KeyCode key,
        SemanticDemonstrationFrame before,
        SemanticDemonstrationFrame after,
        SemanticTargetSelector target) => new()
        {
            Offset = TimeSpan.FromSeconds(offset),
            Category = "키보드",
            Message = $"키 입력 · {key}",
            ActionKind = MacroActionKind.KeyStroke,
            KeyCodes = [key],
            Sequence = sequence,
            CaptureWidth = 1920,
            CaptureHeight = 1080,
            SemanticBefore = before,
            SemanticAfter = after,
            SemanticTarget = target,
            SemanticCaptureId = Guid.NewGuid(),
        };
}
