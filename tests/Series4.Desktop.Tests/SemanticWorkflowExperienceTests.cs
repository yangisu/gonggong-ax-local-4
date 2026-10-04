using Series4.Desktop;
using SharpHook.Data;
using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace Series4.Desktop.Tests;

public sealed class SemanticWorkflowExperienceTests
{
    [Fact]
    public void OneNotepadDemonstration_BecomesAReusableWorkflowThatProducesTheUserOutcome()
    {
        WithVideo(video =>
        {
            var demonstration = new SemanticDemonstration(
                "새 메모를 만들고 회의록이라고 입력해 줘",
                video,
                2,
                [new(0, .5, "MouseLeftClick"), new(1, 1.1, "TextEntry")],
                [
                    Frame("start", .3, Element("Button", "새 메모", "new")),
                    Frame("editor", .8,
                        Element("Button", "새 메모", "new"),
                        Element("Edit", "메모 내용", "editor"),
                        Element("Text", "새 메모 편집 중", "editing")),
                    Frame("typed", 1.3,
                        Element("Button", "새 메모", "new"),
                        Element("Edit", "메모 내용", "editor", "회의록"),
                        Element("Text", "저장됨", "saved")),
                ],
                [
                    new("create-note", 0, .5, "click", Selector("Button", "새 메모", "new"), null, "start", "editor"),
                    new("enter-note", 1, 1.1, "type", Selector("Edit", "메모 내용", "editor"), "회의록", "editor", "typed"),
                ]);

            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
            var surface = new ExperienceSurface(demonstration.Frames, demonstration.Actions, reverseElements: true);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal("회의록", surface.Value("editor"));
            Assert.Equal(["create-note", "enter-note"], surface.ExecutedSteps);
            Assert.Equal(0, surface.WrongTargetExecutions);
            Assert.Equal(1, workflow.EvidenceCoverage.Coverage);
            Assert.Equal(1, workflow.VideoEvidenceCoverage.Coverage);
            Assert.All(workflow.VideoEvidence, evidence => Assert.Equal(64, evidence.VideoFrameSha256.Length));
            Assert.All(result.Journal.Where(item => item.Phase == "verification"),
                item => Assert.StartsWith("success", item.Verification));
        });
    }

    [Fact]
    public void ASeparateSettingsDemonstration_UsesTheSameEngineAndVerifiesTheVisibleResult()
    {
        WithVideo(video =>
        {
            var demonstration = new SemanticDemonstration(
                "설정에서 어두운 모드를 켜 줘",
                video,
                1.5,
                [new(0, .6, "MouseLeftClick")],
                [
                    Frame("light", .4,
                        Element("Button", "어두운 모드", "dark-mode", "끔"),
                        Element("Text", "화면 설정", "heading")),
                    Frame("dark", .9,
                        Element("Button", "어두운 모드", "dark-mode", "켬"),
                        Element("Text", "어두운 모드 사용 중", "status")),
                ],
                [new("enable-dark-mode", 0, .6, "toggle", Selector("Button", "어두운 모드", "dark-mode"), null, "light", "dark")]);

            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
            var surface = new ExperienceSurface(demonstration.Frames, demonstration.Actions);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal("켬", surface.Value("dark-mode"));
            Assert.Contains(surface.Current.Elements, element => element.Name == "어두운 모드 사용 중");
            Assert.Equal(0, surface.WrongTargetExecutions);
        });
    }

    [Fact]
    public void AlreadyAchievedUserState_IsRecognizedWithoutRepeatingTheAction()
    {
        WithVideo(video =>
        {
            var demonstration = DarkModeDemonstration(video);
            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
            var surface = new ExperienceSurface(demonstration.Frames, demonstration.Actions, initialState: 1);

            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Empty(surface.ExecutedSteps);
            Assert.Equal("켬", surface.Value("dark-mode"));
        });
    }

    [Fact]
    public void AmbiguousCurrentTarget_StopsBeforeAnythingVisibleChanges()
    {
        WithVideo(video =>
        {
            var demonstration = DarkModeDemonstration(video);
            var workflow = SemanticWorkflowCompiler.Compile(demonstration);
            var surface = new ExperienceSurface(demonstration.Frames, demonstration.Actions, duplicateTarget: true);

            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("ABSTAINED", result.Status);
            Assert.Empty(surface.ExecutedSteps);
            Assert.Equal("끔", surface.Value("dark-mode"));
            Assert.Equal("not-run", result.Journal[^1].Execution);
        });
    }

    [Fact]
    public void CompilerRejectsAnUnverifiableOutcomeAndIrreversibleIntent()
    {
        WithVideo(video =>
        {
            var unchanged = new SemanticDemonstration(
                "버튼을 눌러 줘", video, 1,
                [new(0, .5, "MouseLeftClick")],
                [Frame("before", .4, Element("Button", "확인", "ok")), Frame("after", .7, Element("Button", "확인", "ok"))],
                [new("press", 0, .5, "click", Selector("Button", "확인", "ok"), null, "before", "after")]);
            var unverifiable = Assert.Throws<InvalidOperationException>(() => SemanticWorkflowCompiler.Compile(unchanged));
            Assert.Contains("구분할 근거", unverifiable.Message);

            var destructive = unchanged with { NaturalLanguageIntent = "계정을 삭제해 줘" };
            var blocked = Assert.Throws<InvalidOperationException>(() => SemanticWorkflowCompiler.Compile(destructive));
            Assert.Contains("비가역", blocked.Message);
        });
    }

    [Fact]
    public void CompilerRejectsAFileThatIsNotAnMp4Recording()
    {
        var video = Path.GetTempFileName();
        File.WriteAllBytes(video, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        try
        {
            var demonstration = DarkModeDemonstration(video);
            var error = Assert.Throws<InvalidOperationException>(() => SemanticWorkflowCompiler.Compile(demonstration));
            Assert.Contains("MP4", error.Message);
        }
        finally { File.Delete(video); }
    }

    [Fact]
    public void CompilerRejectsWorkflowWhenMostReferencedVideoFramesHaveNoPixelHash()
    {
        WithVideo(video =>
        {
            var demonstration = DarkModeDemonstration(video);
            demonstration = demonstration with
            {
                Frames = demonstration.Frames.Select((frame, index) =>
                    index == 0 ? frame : frame with { VideoFrameSha256 = null }).ToArray(),
            };
            var error = Assert.Throws<InvalidOperationException>(() => SemanticWorkflowCompiler.Compile(demonstration));
            Assert.Contains("MP4 프레임 증거", error.Message);
        });
    }

    [Fact]
    public void SourceVideoIntegrity_IsCheckedAgainImmediatelyBeforeExecution()
    {
        WithVideo(video =>
        {
            var workflow = SemanticWorkflowCompiler.Compile(DarkModeDemonstration(video));
            SemanticWorkflowIntegrityVerifier.VerifySourceVideo(workflow);

            File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50, 1]);

            var error = Assert.Throws<InvalidOperationException>(() =>
                SemanticWorkflowIntegrityVerifier.VerifySourceVideo(workflow));
            Assert.Contains("SHA-256", error.Message);
        });
    }

    [Fact]
    public void RecordedClicks_AreExtractedDirectlyIntoAnEvidenceBoundWorkflow()
    {
        WithVideo(video =>
        {
            var firstFrame = Frame("recorded-before-1", .3,
                Element("Button", "다음", "next"), Element("Text", "첫 화면", "state"));
            var secondFrame = Frame("recorded-before-2", .8,
                Element("Button", "확인", "confirm"), Element("Text", "확인 화면", "state"));
            var finalFrame = Frame("recorded-final", 1.4,
                Element("Text", "완료", "done"));
            var recorded = new[]
            {
                RecordedClick(1, .5, firstFrame, Selector("Button", "다음", "next")),
                RecordedClick(2, 1.0, secondFrame, Selector("Button", "확인", "confirm")),
            };

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "두 화면을 진행해 완료 상태로 만들어 줘", video, 1.5, recorded, finalFrame);
            var surface = new WorkflowFrameSurface([firstFrame, secondFrame, finalFrame]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal("완료", surface.Current.Elements.Single(element => element.AutomationId == "done").Name);
            Assert.Equal(2, workflow.Steps.Count);
            Assert.Equal(1, workflow.EvidenceCoverage.Coverage);
            Assert.Equal(["recorded-1", "recorded-2"], surface.ExecutedSteps);
        });
    }

    [Fact]
    public void RecordedKeystrokes_AreGroupedByObservedEditorValueAndReproduceTheVisibleText()
    {
        WithVideo(video =>
        {
            var start = Frame("start", .2, Element("Button", "새 메모", "new"));
            var empty = Frame("key-empty", .6, Element("Edit", "메모 내용", "editor", ""));
            var one = Frame("key-one", .72, Element("Edit", "메모 내용", "editor", "회"));
            var two = Frame("key-two", .84, Element("Edit", "메모 내용", "editor", "회의"));
            var typed = Frame("key-three", .96, Element("Edit", "메모 내용", "editor", "회의록"));
            var final = Frame("final", 1.3,
                Element("Edit", "메모 내용", "editor", "회의록"),
                Element("Text", "저장됨", "saved"));
            var target = Selector("Edit", "메모 내용", "editor");
            var recorded = new[]
            {
                RecordedClick(1, .3, start, Selector("Button", "새 메모", "new")),
                RecordedKey(2, .65, KeyCode.VcH, empty, one, target),
                RecordedKey(3, .77, KeyCode.VcO, one, two, target),
                RecordedKey(4, .89, KeyCode.VcI, two, typed, target),
            };

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "새 메모를 만들고 회의록을 입력해 줘", video, 1.5, recorded, final);
            var surface = new WorkflowFrameSurface([start, empty, final]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal("회의록", surface.Current.Elements.Single(element => element.AutomationId == "editor").Value);
            Assert.Equal(["recorded-1", "recorded-2"], surface.ExecutedSteps);
            Assert.Equal([1, 2, 3], workflow.Steps[1].Evidence.EventIndices);
            Assert.Equal(1, workflow.EvidenceCoverage.Coverage);
        });
    }

    [Fact]
    public void RecordedWheelEvents_BecomeOneSemanticScrollWithAVisibleOutcome()
    {
        WithVideo(video =>
        {
            var target = Selector("List", "업무 목록", "work-list");
            var before = Frame("scroll-start", .4,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=0"),
                Element("Text", "목록 시작", "scroll-status"));
            var middle = Frame("scroll-middle", .65,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=8"));
            var after = Frame("scroll-end", .9,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=16"),
                Element("Text", "아래 항목 표시됨", "scroll-status"));
            var recorded = new[]
            {
                RecordedWheel(1, .5, -120, before, middle, target),
                RecordedWheel(2, .75, -120, middle, after, target),
            };

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "업무 목록을 내려 아래 항목을 보여 줘", video, 1.2, recorded, after);
            var surface = new WorkflowFrameSurface([before, after]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            var step = Assert.Single(workflow.Steps);
            Assert.Equal("scroll", step.Action.Kind);
            Assert.Equal("vertical:increment:2", step.Action.Value);
            Assert.Equal([0, 1], step.Evidence.EventIndices);
            Assert.Contains(surface.Current.Elements, element => element.Name == "아래 항목 표시됨");
        });
    }

    [Fact]
    public void ScrollWithOnlyAPercentageChange_IsRejectedAsUnverifiableUserOutcome()
    {
        WithVideo(video =>
        {
            var target = Selector("List", "업무 목록", "work-list");
            var before = Frame("scroll-only-start", .4,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=0"));
            var after = Frame("scroll-only-end", .9,
                Element("List", "업무 목록", "work-list", "horizontal=-1;vertical=15"));

            var error = Assert.Throws<InvalidOperationException>(() =>
                RecordedSemanticWorkflowExtractor.Compile(
                    "업무 목록을 아래로 내려 줘",
                    video,
                    1.2,
                    [RecordedWheel(1, .5, -120, before, after, target)],
                    after));

            Assert.Contains("구분할 근거", error.Message);
        });
    }

    [Fact]
    public void RecordedSpaceOnACheckbox_BecomesASemanticToggleAndVerifiesTheUserResult()
    {
        WithVideo(video =>
        {
            var target = Selector("CheckBox", "어두운 모드", "dark-mode");
            var before = Frame("keyboard-light", .3,
                Element("CheckBox", "어두운 모드", "dark-mode", "Off"),
                Element("Text", "밝은 모드 사용 중", "mode-status"));
            var after = Frame("keyboard-dark", .6,
                Element("CheckBox", "어두운 모드", "dark-mode", "On"),
                Element("Text", "어두운 모드 사용 중", "mode-status"));
            var recorded = RecordedKey(1, .45, KeyCode.VcSpace, before, after, target);

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "키보드로 어두운 모드를 켜 줘", video, 1, [recorded], after);
            var surface = new WorkflowFrameSurface([before, after]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            var step = Assert.Single(workflow.Steps);
            Assert.Equal("toggle", step.Action.Kind);
            Assert.Equal([0], step.Evidence.EventIndices);
            Assert.Contains(surface.Current.Elements, element => element.Name == "어두운 모드 사용 중");
        });
    }

    [Fact]
    public void RecordedEnterOnAButton_BecomesASemanticClick()
    {
        WithVideo(video =>
        {
            var target = Selector("Button", "다음", "next");
            var before = Frame("enter-before", .3,
                Element("Button", "다음", "next"),
                Element("Text", "첫 화면", "status"));
            var after = Frame("enter-after", .6,
                Element("Text", "완료", "status"));

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "키보드로 다음 화면을 열어 줘",
                video,
                1,
                [RecordedKey(1, .45, KeyCode.VcEnter, before, after, target)],
                after);
            var surface = new WorkflowFrameSurface([before, after]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal("click", Assert.Single(workflow.Steps).Action.Kind);
            Assert.Contains(surface.Current.Elements, element => element.Name == "완료");
        });
    }

    [Fact]
    public void ModifiedKeyboardActivation_IsNotPromotedToAnUnattendedAction()
    {
        WithVideo(video =>
        {
            var target = Selector("Button", "다음", "next");
            var before = Frame("activation-before", .3, Element("Button", "다음", "next"));
            var after = Frame("activation-after", .6, Element("Text", "완료", "done"));
            var recorded = RecordedKey(1, .45, KeyCode.VcEnter, before, after, target);
            recorded.ModifierKeyCodes = [KeyCode.VcLeftAlt];

            var error = Assert.Throws<InvalidOperationException>(() =>
                RecordedSemanticWorkflowExtractor.Compile(
                    "다음 화면을 열어 줘", video, 1, [recorded], after));

            Assert.Contains("Ctrl/Alt/Win", error.Message);
        });
    }

    [Fact]
    public void RecordedSliderDrag_BecomesASetRangeActionWithAVisibleUserOutcome()
    {
        WithVideo(video =>
        {
            var target = Selector("Slider", "음량", "volume-slider");
            var before = Frame("range-before", .3,
                Element("Slider", "음량", "volume-slider", "20"),
                Element("Text", "음량 20", "volume-status"));
            var after = Frame("range-after", .8,
                Element("Slider", "음량", "volume-slider", "75"),
                Element("Text", "음량 75", "volume-status"));

            var workflow = RecordedSemanticWorkflowExtractor.Compile(
                "음량을 75로 높여 줘",
                video,
                1.2,
                [RecordedDrag(1, .5, before, after, target)],
                after);
            var surface = new WorkflowFrameSurface([before, after]);
            var result = new SemanticWorkflowRunner().Run(workflow, surface, verificationDelay: TimeSpan.Zero);

            Assert.Equal("SUCCESS", result.Status);
            var step = Assert.Single(workflow.Steps);
            Assert.Equal("set-range", step.Action.Kind);
            Assert.Equal("75", step.Action.Value);
            Assert.Contains(surface.Current.Elements, element => element.Name == "음량 75");
        });
    }

    [Fact]
    public void CanvasDragWithoutASemanticRangeValue_IsRejected()
    {
        WithVideo(video =>
        {
            var target = Selector("Pane", "그리기 영역", "canvas");
            var before = Frame("canvas-before", .3, Element("Pane", "그리기 영역", "canvas"));
            var after = Frame("canvas-after", .8, Element("Pane", "그리기 영역", "canvas"));

            var error = Assert.Throws<InvalidOperationException>(() =>
                RecordedSemanticWorkflowExtractor.Compile(
                    "선을 그어 줘",
                    video,
                    1.2,
                    [RecordedDrag(1, .5, before, after, target)],
                    after));

            Assert.Contains("슬라이더 드래그", error.Message);
        });
    }

    [Fact]
    public void RecordedSensitiveOrModifiedKeys_AreNotPromotedToUnattendedTextActions()
    {
        WithVideo(video =>
        {
            var before = Frame("before", .3, Element("Edit", "비밀번호", "password", ""));
            var after = Frame("after", .5, Element("Edit", "비밀번호", "password", "x"));
            var sensitive = RecordedKey(1, .4, KeyCode.VcX, before, after, Selector("Edit", "비밀번호", "password"));
            var final = Frame("final", .8, Element("Edit", "비밀번호", "password", "x"));
            var error = Assert.Throws<InvalidOperationException>(() => RecordedSemanticWorkflowExtractor.Compile(
                "암호를 입력해 줘", video, 1, [sensitive], final));
            Assert.Contains("민감 입력", error.Message);

            var ordinaryBefore = Frame("ordinary-before", .3, Element("Edit", "검색", "search", ""));
            var ordinaryAfter = Frame("ordinary-after", .5, Element("Edit", "검색", "search", "v"));
            var modified = RecordedKey(2, .4, KeyCode.VcV, ordinaryBefore, ordinaryAfter, Selector("Edit", "검색", "search"));
            modified.ModifierKeyCodes = [KeyCode.VcLeftControl];
            var modifiedError = Assert.Throws<InvalidOperationException>(() => RecordedSemanticWorkflowExtractor.Compile(
                "검색어를 입력해 줘", video, 1, [modified], ordinaryAfter));
            Assert.Contains("Ctrl/Alt/Win", modifiedError.Message);
        });
    }

    [Fact]
    public async Task SemanticRecordingEvidence_SurvivesProjectSaveAndReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"series4-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "recording.mp4");
        var sidecar = Path.Combine(directory, "recording.series4.json");
        File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]);
        var frame = Frame("before", .4, Element("Button", "다음", "next"));
        var recorded = RecordedClick(1, .5, frame, Selector("Button", "다음", "next"));
        try
        {
            await MacroProjectStore.SaveToPathAsync(sidecar, video, [recorded]);
            var loaded = await MacroProjectStore.ReadAsync(sidecar);

            var restored = Assert.Single(loaded.RecordedEvents);
            Assert.Equal("before", restored.SemanticBefore?.Id);
            Assert.Equal("next", restored.SemanticTarget?.AutomationId);
            Assert.Equal("다음", restored.SemanticTarget?.Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static SemanticDemonstration DarkModeDemonstration(string video) => new(
        "설정에서 어두운 모드를 켜 줘",
        video,
        1.5,
        [new(0, .6, "MouseLeftClick")],
        [
            Frame("light", .4,
                Element("Button", "어두운 모드", "dark-mode", "끔"),
                Element("Text", "화면 설정", "heading")),
            Frame("dark", .9,
                Element("Button", "어두운 모드", "dark-mode", "켬"),
                Element("Text", "어두운 모드 사용 중", "status")),
        ],
        [new("enable-dark-mode", 0, .6, "toggle", Selector("Button", "어두운 모드", "dark-mode"), null, "light", "dark")]);

    private static SemanticDemonstrationFrame Frame(string id, double offset, params SemanticElementEvidence[] elements) =>
        new(id, offset, "SampleApp", "업무 앱", string.Empty, elements, FrameHash(id));

    private static string FrameHash(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();

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

    private static void WithVideo(Action<string> action)
    {
        var video = Path.GetTempFileName();
        File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]);
        try { action(video); }
        finally { File.Delete(video); }
    }

    private sealed class ExperienceSurface : ISemanticWorkflowSurface
    {
        private readonly IReadOnlyList<SemanticDemonstrationFrame> frames;
        private readonly IReadOnlyList<SemanticDemonstrationAction> actions;
        private readonly bool reverseElements;
        private readonly bool duplicateTarget;
        private int state;
        private long revision;

        internal ExperienceSurface(
            IReadOnlyList<SemanticDemonstrationFrame> frames,
            IReadOnlyList<SemanticDemonstrationAction> actions,
            bool reverseElements = false,
            int initialState = 0,
            bool duplicateTarget = false)
        {
            this.frames = frames;
            this.actions = actions;
            this.reverseElements = reverseElements;
            this.duplicateTarget = duplicateTarget;
            state = initialState;
        }

        internal List<string> ExecutedSteps { get; } = [];
        internal int WrongTargetExecutions { get; private set; }
        internal SemanticWorkflowObservation Current => Observe();
        internal string Value(string automationId) => Current.Elements.First(element => element.AutomationId == automationId).Value;

        public SemanticWorkflowObservation Observe()
        {
            var frame = frames[state];
            var elements = frame.Elements.Select((element, index) => new SemanticWorkflowElement(
                $"{element.AutomationId}-{index}", element.Role, element.Name, element.AutomationId,
                element.Value, element.Enabled, element.Offscreen, element.Password)).ToList();
            if (duplicateTarget && state == 0)
            {
                var original = elements.Single(element => element.AutomationId == actions[0].Target!.AutomationId);
                elements.Add(original with { Id = original.Id + "-duplicate" });
            }
            if (reverseElements) elements.Reverse();
            return new SemanticWorkflowObservation(frame.ProcessName, frame.WindowTitle, frame.Url, elements, revision);
        }

        public void Execute(SemanticPlannedAction action, SemanticWorkflowObservation observation)
        {
            var expected = actions[state];
            var current = Observe();
            var candidate = current.Elements.SingleOrDefault(element => element.Id == action.TargetId);
            if (observation.Revision != revision
                || action.StepId != expected.Id
                || candidate is null
                || candidate.AutomationId != expected.Target!.AutomationId)
            {
                WrongTargetExecutions++;
                throw new InvalidOperationException("wrong or stale target");
            }
            ExecutedSteps.Add(action.StepId);
            state++;
            revision++;
        }
    }

    private sealed class WorkflowFrameSurface(IReadOnlyList<SemanticDemonstrationFrame> frames) : ISemanticWorkflowSurface
    {
        private int state;
        private long revision;
        internal List<string> ExecutedSteps { get; } = [];
        internal SemanticWorkflowObservation Current => Observe();

        public SemanticWorkflowObservation Observe()
        {
            var frame = frames[state];
            return new SemanticWorkflowObservation(
                frame.ProcessName,
                frame.WindowTitle,
                frame.Url,
                frame.Elements.Select((element, index) => new SemanticWorkflowElement(
                    $"{element.AutomationId}-{index}", element.Role, element.Name, element.AutomationId,
                    element.Value, element.Enabled, element.Offscreen, element.Password)).ToArray(),
                revision);
        }

        public void Execute(SemanticPlannedAction action, SemanticWorkflowObservation observation)
        {
            Assert.Equal(revision, observation.Revision);
            Assert.Contains(observation.Elements, element => element.Id == action.TargetId);
            ExecutedSteps.Add(action.StepId);
            state++;
            revision++;
        }
    }
}
