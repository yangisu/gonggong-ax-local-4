using Series4.Desktop;
using Xunit;

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
        new(id, offset, "SampleApp", "업무 앱", string.Empty, elements);

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
