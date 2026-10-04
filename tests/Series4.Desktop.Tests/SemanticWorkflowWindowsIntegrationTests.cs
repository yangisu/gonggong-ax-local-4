using System.Diagnostics;
using System.Runtime.InteropServices;
using Series4.Desktop;
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
            var demonstration = new SemanticDemonstration(
                "새 메모를 만들고 회의록이라고 입력해 줘", video, 2,
                [new(0, .5, "MouseLeftClick"), new(1, 1.1, "TextEntry")],
                [
                    Frame("start", .3, Element("Button", "새 메모", "new-note"), Element("Text", "메모 업무", "heading")),
                    Frame("editor", .8,
                        Element("Edit", "메모 내용", "note-editor"),
                        Element("Text", "새 메모 편집 중", "heading"),
                        Element("Text", "입력 대기", "note-status")),
                    Frame("typed", 1.3,
                        Element("Edit", "메모 내용", "note-editor", "회의록"),
                        Element("Text", "새 메모 편집 중", "heading"),
                        Element("Text", "저장됨", "note-status")),
                ],
                [
                    new("create-note", 0, .5, "click", Selector("Button", "새 메모", "new-note"), null, "start", "editor"),
                    new("enter-note", 1, 1.1, "type", Selector("Edit", "메모 내용", "note-editor"), "회의록", "editor", "typed"),
                ]);
            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
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
            var demonstration = new SemanticDemonstration(
                "설정에서 어두운 모드를 켜 줘", video, 1.5,
                [new(0, .6, "MouseLeftClick")],
                [
                    Frame("light", .4,
                        Element("CheckBox", "어두운 모드", "dark-mode", "Off"),
                        Element("Text", "밝은 모드 사용 중", "mode-status")),
                    Frame("dark", .9,
                        Element("CheckBox", "어두운 모드", "dark-mode", "On"),
                        Element("Text", "어두운 모드 사용 중", "mode-status")),
                ],
                [new("enable-dark-mode", 0, .6, "toggle", Selector("CheckBox", "어두운 모드", "dark-mode"), null, "light", "dark")]);
            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
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
}
