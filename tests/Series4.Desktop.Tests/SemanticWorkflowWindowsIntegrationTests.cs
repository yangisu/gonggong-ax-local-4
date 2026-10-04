using System.Diagnostics;
using System.Runtime.InteropServices;
using Series4.Desktop;
using SharpHook.Data;
using Xunit;
using System.Security.Cryptography;
using System.Text;

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

    [WindowsIntegrationFact]
    public void RecordedScrollTask_RevealsTheDemonstratedUserOutcomeInARealWindow()
    {
        RunWithFixture("scroll", video =>
        {
            var target = Selector("List", "업무 목록", "work-list");
            var before = Frame("scroll-start", .4,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=0"),
                Element("Text", "목록 시작", "scroll-status"));
            var after = Frame("scroll-end", .9,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=10"),
                Element("Text", "아래 항목 표시됨", "scroll-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "업무 목록을 내려 아래 항목을 보여 줘", video, 1.5,
                [RecordedWheel(1, .6, -120, before, after, target)],
                after);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.True(result.Status == "SUCCESS", result.ToJson());
            Assert.Contains(visible.Elements, element => element.Name == "아래 항목 표시됨");
        });
    }

    [WindowsIntegrationFact]
    public void RecordedKeyboardActivation_ProducesTheDemonstratedToggleResultInARealWindow()
    {
        RunWithFixture("settings", video =>
        {
            var target = Selector("CheckBox", "어두운 모드", "dark-mode");
            var before = Frame("keyboard-light", .3,
                Element("CheckBox", "어두운 모드", "dark-mode", "Off"),
                Element("Text", "밝은 모드 사용 중", "mode-status"));
            var after = Frame("keyboard-dark", .7,
                Element("CheckBox", "어두운 모드", "dark-mode", "On"),
                Element("Text", "어두운 모드 사용 중", "mode-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "키보드로 어두운 모드를 켜 줘", video, 1,
                [RecordedKey(1, .5, KeyCode.VcSpace, before, after, target)],
                after);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.True(result.Status == "SUCCESS", result.ToJson());
            Assert.Contains(visible.Elements, element => element.AutomationId == "dark-mode" && element.Value == "On");
            Assert.Contains(visible.Elements, element => element.Name == "어두운 모드 사용 중");
        });
    }

    [WindowsIntegrationFact]
    public void RecordedSliderDrag_ProducesTheDemonstratedRangeValueInARealWindow()
    {
        RunWithFixture("slider", video =>
        {
            var target = Selector("Slider", "음량", "volume-slider");
            var before = Frame("range-before", .3,
                Element("Slider", "음량", "volume-slider", "20"),
                Element("Text", "음량 20", "volume-status"));
            var after = Frame("range-after", .8,
                Element("Slider", "음량", "volume-slider", "75"),
                Element("Text", "음량 75", "volume-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "음량을 75로 높여 줘", video, 1.2,
                [RecordedDrag(1, .5, before, after, target)],
                after);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.True(result.Status == "SUCCESS", result.ToJson());
            Assert.Contains(visible.Elements, element => element.AutomationId == "volume-slider" && element.Value == "75");
            Assert.Contains(visible.Elements, element => element.Name == "음량 75");
        });
    }

    [WindowsIntegrationFact]
    public void RecordedArrowNavigation_SelectsTheDemonstratedOptionInARealWindow()
    {
        RunWithFixture("selection", video =>
        {
            var target = Selector("ComboBox", "색상", "color-selector");
            var before = Frame("select-red", .3,
                Element("ComboBox", "색상", "color-selector", "빨강"),
                Element("Text", "선택: 빨강", "color-status"));
            var middle = Frame("select-green", .55,
                Element("ComboBox", "색상", "color-selector", "초록"));
            var after = Frame("select-blue", .8,
                Element("ComboBox", "색상", "color-selector", "파랑"),
                Element("Text", "선택: 파랑", "color-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "색상을 파랑으로 선택해 줘", video, 1.2,
                [
                    RecordedKey(1, .45, KeyCode.VcDown, before, middle, target),
                    RecordedKey(2, .7, KeyCode.VcDown, middle, after, target),
                ],
                after);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.True(result.Status == "SUCCESS", result.ToJson());
            Assert.Contains(visible.Elements, element => element.AutomationId == "color-selector" && element.Value == "파랑");
            Assert.Contains(visible.Elements, element => element.Name == "선택: 파랑");
        });
    }

    [WindowsIntegrationFact]
    public void RecordedTabTraversal_FocusesTheDemonstratedControlInARealWindow()
    {
        RunWithFixture("focus", video =>
        {
            var name = Selector("Edit", "이름", "focus-name");
            var email = Selector("Edit", "이메일", "focus-email");
            var before = Frame("focus-name", .3,
                Element("Edit", "이름", "focus-name", focused: true),
                Element("Edit", "이메일", "focus-email"),
                Element("Button", "검색", "focus-search"),
                Element("Text", "포커스: 이름", "focus-status"));
            var middle = Frame("focus-email", .55,
                Element("Edit", "이름", "focus-name"),
                Element("Edit", "이메일", "focus-email", focused: true),
                Element("Button", "검색", "focus-search"),
                Element("Text", "포커스: 이메일", "focus-status"));
            var after = Frame("focus-search", .8,
                Element("Edit", "이름", "focus-name"),
                Element("Edit", "이메일", "focus-email"),
                Element("Button", "검색", "focus-search", focused: true),
                Element("Text", "포커스: 검색", "focus-status"));
            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "Tab으로 검색 버튼까지 이동해 줘",
                video,
                1.2,
                [
                    RecordedKey(1, .45, KeyCode.VcTab, before, middle, name),
                    RecordedKey(2, .7, KeyCode.VcTab, middle, after, email),
                ],
                after);
            var surface = new WindowsSemanticWorkflowSurface();

            var result = new SemanticWorkflowRunner().Run(workflow, surface);
            var visible = surface.Observe();

            Assert.True(result.Status == "SUCCESS", result.ToJson());
            Assert.Contains(visible.Elements,
                element => element.AutomationId == "focus-search" && element.KeyboardFocused);
            Assert.Contains(visible.Elements, element => element.Name == "포커스: 검색");
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
        new(id, offset, "SemanticWorkflowFixture", "Semantic Workflow UX Fixture", string.Empty, elements, FrameHash(id));

    private static string FrameHash(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();

    private static SemanticElementEvidence Element(
        string role,
        string name,
        string automationId,
        string value = "",
        bool focused = false) =>
        new(role, name, automationId, value, KeyboardFocused: focused);

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

    private static RecordedEvent RecordedWheel(
        long sequence,
        double offset,
        int rotation,
        SemanticDemonstrationFrame before,
        SemanticDemonstrationFrame after,
        SemanticTargetSelector target) => new()
        {
            Offset = TimeSpan.FromSeconds(offset),
            Category = "마우스",
            Message = "휠 아래로",
            ActionKind = MacroActionKind.MouseWheel,
            WheelRotation = rotation,
            Sequence = sequence,
            CaptureWidth = 1920,
            CaptureHeight = 1080,
            SemanticBefore = before,
            SemanticAfter = after,
            SemanticTarget = target,
            SemanticCaptureId = Guid.NewGuid(),
        };

    private static RecordedEvent RecordedDrag(
        long sequence,
        double offset,
        SemanticDemonstrationFrame before,
        SemanticDemonstrationFrame after,
        SemanticTargetSelector target) => new()
        {
            Offset = TimeSpan.FromSeconds(offset),
            Category = "마우스",
            Message = "왼쪽 드래그",
            ActionKind = MacroActionKind.MouseDrag,
            DragButton = MouseButton.Button1,
            DragDuration = TimeSpan.FromMilliseconds(400),
            Sequence = sequence,
            CaptureWidth = 1920,
            CaptureHeight = 1080,
            SemanticBefore = before,
            SemanticAfter = after,
            SemanticTarget = target,
            SemanticCaptureId = Guid.NewGuid(),
        };
}
