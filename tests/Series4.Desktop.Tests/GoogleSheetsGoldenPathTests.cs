using Series4.Desktop;
using Xunit;

namespace Series4.Desktop.Tests;

public sealed class GoogleSheetsGoldenPathTests
{
    [Fact]
    public void Compiler_BindsNaturalLanguageAndEveryGoldenPathStepToEvidence()
    {
        var events = Enumerable.Range(0, 4).Select(index => new DemonstrationInputEvent(index, index + .5, "MouseLeftClick")).ToArray();
        var screens = Enumerable.Range(0, 4).Select(index => new DemonstrationScreenEvidence($"f{index}", index + .5, [$"state-{index}"])).ToArray();
        var links = new[]
        {
            new WorkflowEvidenceLink("open-google-apps-menu", [0], ["f0"]),
            new WorkflowEvidenceLink("open-sheets", [1], ["f1"]),
            new WorkflowEvidenceLink("create-blank-spreadsheet", [2], ["f2"]),
            new WorkflowEvidenceLink("close-created-spreadsheet-tab", [3], ["f3"]),
        };
        var workflow = GoogleSheetsWorkflowCompiler.Compile("Google Sheets에서 빈 스프레드시트를 만들고 탭을 닫아줘", 5, events, screens, links);
        Assert.Equal(1, workflow.EvidenceCoverage.Coverage);
        Assert.Equal(4, workflow.Steps.Count);
        Assert.All(workflow.Steps, step => Assert.NotEmpty(step.Evidence.ScreenEvidenceIds));
    }

    [Fact]
    public void Compiler_RejectsMissingGoldenPathEvidenceLink()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GoogleSheetsWorkflowCompiler.Compile(
            "빈 스프레드시트를 만들어줘", 2,
            [new(0, 1, "MouseLeftClick")],
            [new("f0", 1, ["Google apps"])],
            [new("open-google-apps-menu", [0], ["f0"])]));
        Assert.Contains("모든 실행 단계", error.Message);
    }

    [Fact]
    public void DemonstrationEvidence_RejectsMissingEventsBeforeWorkflowCreation()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DemonstrationEvidenceValidator.Validate(10, [], [new("f0", 0, ["Google"])], []));
        Assert.Contains("입력 이벤트", error.Message);
    }

    [Fact]
    public void DemonstrationEvidence_RejectsVideoEventMismatch()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DemonstrationEvidenceValidator.Validate(
            10,
            [new(0, 8, "MouseLeftClick")],
            [new("f0", 1, ["Google apps"])],
            [new("open-menu", [0], ["f0"])],
            maximumSynchronizationSkewSeconds: .5));
        Assert.Contains("시간 차이", error.Message);
    }

    [Fact]
    public void DemonstrationEvidence_RequiresEightyPercentCoverageAndBothEvidenceKinds()
    {
        var events = Enumerable.Range(0, 5).Select(index => new DemonstrationInputEvent(index, index, "MouseLeftClick")).ToArray();
        var screens = Enumerable.Range(0, 5).Select(index => new DemonstrationScreenEvidence($"f{index}", index, ["semantic state"])).ToArray();
        Assert.Throws<InvalidOperationException>(() => DemonstrationEvidenceValidator.Validate(6, events, screens, [new("only", [0, 1, 2], ["f0"])]));
        var result = DemonstrationEvidenceValidator.Validate(6, events, screens,
        [
            new("open-menu", [0], ["f0"]), new("open-sheets", [1], ["f1"]),
            new("blank", [2], ["f2"]), new("close", [3], ["f3"]),
        ]);
        Assert.Equal(.8, result.Coverage, 3);
    }

    [Fact]
    public void DemonstrationEvidence_RejectsEmptyScreenSemantics()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DemonstrationEvidenceValidator.Validate(
            2,
            [new(0, 1, "MouseLeftClick")],
            [new("f0", 1, [])],
            [new("open-menu", [0], ["f0"])]));
        Assert.Contains("의미 정보", error.Message);
    }

    [Fact]
    public void MultipleActiveTabs_AreUnverifiableAndNeverExecuted()
    {
        var observation = new GoogleSheetsObservation("chrome",
        [
            new("one", "Google", "https://www.google.com/", true),
            new("two", "Google", "https://www.google.com/", true),
        ], [new("apps", "button", "Google apps")], 1);
        var surface = new StaticSurface(observation);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface);
        Assert.Equal("ABSTAINED", result.Status);
        Assert.Equal(0, surface.ExecutionCount);
    }

    private sealed class StaticSurface(GoogleSheetsObservation observation) : IGoogleSheetsSurface
    {
        public int ExecutionCount { get; private set; }
        public GoogleSheetsObservation Observe() => observation;
        public void Execute(GoldenPathAction action, GoogleSheetsObservation before) => ExecutionCount++;
    }

    [Fact]
    public void StateGraph_CompletesTenLayoutAndTabOrderVariantsWithoutWrongTarget()
    {
        for (var variant = 0; variant < 10; variant++)
        {
            var surface = new FakeSurface(variant);
            var result = new GoogleSheetsGoldenPathRunner().Run(surface);
            Assert.Equal("SUCCESS", result.Status);
            Assert.Equal(GoogleSheetsState.CreatedSpreadsheetTabClosed, result.State);
            Assert.Equal(0, surface.WrongTargetExecutions);
            Assert.Equal(4, surface.Executions.Count);
            Assert.All(result.Journal.Where(item => item.Phase == "verification"), item => Assert.StartsWith("success", item.Verification));
        }
    }

    [Fact]
    public void StateGraph_SkipsAlreadyCompletedStates()
    {
        var surface = new FakeSurface(3, GoogleSheetsState.SheetsHome);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface);
        Assert.Equal("SUCCESS", result.Status);
        Assert.Equal(["create-blank-spreadsheet", "close-created-spreadsheet-tab"], surface.Executions);
    }

    [Fact]
    public void AmbiguousSheetsCandidate_AbstainsWithoutClicking()
    {
        var surface = new FakeSurface(0, duplicateSheetsCandidate: true);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface);
        Assert.Equal("ABSTAINED", result.Status);
        Assert.Empty(surface.Executions);
        Assert.Equal(2, result.Journal[^1].Candidates.Count);
        Assert.Equal("not-run", result.Journal[^1].Execution);
    }

    [Fact]
    public void WrongChromeTab_AbstainsWithoutClicking()
    {
        var surface = new FakeSurface(0, GoogleSheetsState.Unknown);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface);
        Assert.Equal("ABSTAINED", result.Status);
        Assert.Empty(surface.Executions);
        Assert.Contains("semantic state", result.Journal[^1].Decision);
    }

    [Fact]
    public void FailedVerification_JournalContainsObservationDecisionExecutionAndVerification()
    {
        var surface = new FakeSurface(0, keepStateAfterExecution: true);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface);
        Assert.Equal("VERIFICATION_FAILED", result.Status);
        var failure = result.Journal[^1];
        Assert.NotEmpty(failure.Observation);
        Assert.NotEmpty(failure.Candidates);
        Assert.NotEmpty(failure.Decision);
        Assert.Equal("executed", failure.Execution);
        Assert.StartsWith("expected", failure.Verification);
        Assert.Contains("VERIFICATION_FAILED", result.ToJson());
    }

    [Fact]
    public void Verification_ReobservesDelayedSemanticStateWithoutRepeatingInput()
    {
        var inner = new FakeSurface(0);
        var surface = new DelayedObservationSurface(inner, observationsToHoldAfterExecution: 2);
        var result = new GoogleSheetsGoldenPathRunner().Run(surface, verificationDelay: TimeSpan.Zero);
        Assert.Equal("SUCCESS", result.Status);
        Assert.Equal(4, inner.Executions.Count);
        Assert.All(result.Journal.Where(item => item.Phase == "verification"), item => Assert.Contains("observation", item.Verification));
    }

    private sealed class DelayedObservationSurface(IGoogleSheetsSurface inner, int observationsToHoldAfterExecution) : IGoogleSheetsSurface
    {
        private GoogleSheetsObservation? held;
        private int remaining;

        public GoogleSheetsObservation Observe()
        {
            if (held is not null && remaining-- > 0) return held;
            held = null;
            return inner.Observe();
        }

        public void Execute(GoldenPathAction action, GoogleSheetsObservation observation)
        {
            held = observation;
            remaining = observationsToHoldAfterExecution;
            inner.Execute(action, observation);
        }
    }

    private sealed class FakeSurface : IGoogleSheetsSurface
    {
        private readonly int variant;
        private readonly bool duplicateSheetsCandidate;
        private readonly bool keepStateAfterExecution;
        private GoogleSheetsState state;
        private long revision;
        private readonly string sourceTab = "google-source";
        private readonly string createdTab = "created-sheet";
        public List<string> Executions { get; } = [];
        public int WrongTargetExecutions { get; private set; }

        internal FakeSurface(int variant, GoogleSheetsState initial = GoogleSheetsState.GoogleHome, bool duplicateSheetsCandidate = false, bool keepStateAfterExecution = false)
        {
            this.variant = variant;
            state = initial;
            this.duplicateSheetsCandidate = duplicateSheetsCandidate;
            this.keepStateAfterExecution = keepStateAfterExecution;
        }

        public GoogleSheetsObservation Observe()
        {
            var noise = Enumerable.Range(0, variant % 4).Select(index => new SemanticElement($"noise-{index}", "button", $"Other {index}")).ToList();
            List<BrowserTab> tabs;
            List<SemanticElement> elements = noise;
            switch (state)
            {
                case GoogleSheetsState.GoogleHome:
                    tabs = [new(sourceTab, "Google", "https://www.google.com/", true), new("other", "Mail", "https://mail.google.com/", false)];
                    elements.Add(new("apps", "button", "Google apps"));
                    if (duplicateSheetsCandidate) elements.Add(new("apps-duplicate", "button", "Google apps"));
                    break;
                case GoogleSheetsState.AppsMenuOpen:
                    tabs = [new(sourceTab, "Google", "https://www.google.com/", true), new("other", "Mail", "https://mail.google.com/", false)];
                    elements.Add(new("menu", "menu", "Google apps"));
                    elements.Add(new("sheets", "link", "Sheets"));
                    break;
                case GoogleSheetsState.SheetsHome:
                    tabs = [new(sourceTab, "Google Sheets", "https://docs.google.com/spreadsheets/u/0/", true), new("other", "Mail", "https://mail.google.com/", false)];
                    elements.Add(new("blank", "button", "Blank spreadsheet"));
                    break;
                case GoogleSheetsState.BlankSpreadsheetOpen:
                    tabs = variant % 2 == 0
                        ? [new(sourceTab, "Google Sheets", "https://docs.google.com/spreadsheets/u/0/", false), new(createdTab, "Untitled spreadsheet", "https://docs.google.com/spreadsheets/d/abc123/edit", true)]
                        : [new(sourceTab, "Untitled spreadsheet", "https://docs.google.com/spreadsheets/d/abc123/edit", true), new("other", "Mail", "https://mail.google.com/", false)];
                    elements.Add(new("grid", "grid", "Sheet1"));
                    elements.Add(new("formula", "textbox", "Formula bar"));
                    break;
                case GoogleSheetsState.CreatedSpreadsheetTabClosed:
                    tabs = variant % 2 == 0
                        ? [new(sourceTab, "Google Sheets", "https://docs.google.com/spreadsheets/u/0/", true), new("other", "Mail", "https://mail.google.com/", false)]
                        : [new("other", "Mail", "https://mail.google.com/", true)];
                    elements.Add(new("blank", "button", "Blank spreadsheet"));
                    break;
                default:
                    tabs = [new("wrong", "Google AI Studio", "https://aistudio.google.com/", true), new(sourceTab, "Google", "https://www.google.com/", false)];
                    elements.Add(new("similar", "button", "Google AI Studio apps"));
                    break;
            }
            if (variant % 2 == 1) tabs.Reverse();
            if (variant % 3 == 1) elements.Reverse();
            return new GoogleSheetsObservation("chrome", tabs, elements, revision);
        }

        public void Execute(GoldenPathAction action, GoogleSheetsObservation observation)
        {
            if (observation.Revision != revision) throw new InvalidOperationException("STALE_ACTION");
            var expected = state switch
            {
                GoogleSheetsState.GoogleHome => ("open-google-apps-menu", "apps"),
                GoogleSheetsState.AppsMenuOpen => ("open-sheets", "sheets"),
                GoogleSheetsState.SheetsHome => ("create-blank-spreadsheet", "blank"),
                GoogleSheetsState.BlankSpreadsheetOpen => ("close-created-spreadsheet-tab", (string?)null),
                _ => ("none", (string?)null),
            };
            if (action.StepId != expected.Item1 || action.TargetId != expected.Item2)
            {
                WrongTargetExecutions++;
                throw new InvalidOperationException("wrong target");
            }
            Executions.Add(action.StepId);
            revision++;
            if (!keepStateAfterExecution)
                state = state switch
                {
                    GoogleSheetsState.GoogleHome => GoogleSheetsState.AppsMenuOpen,
                    GoogleSheetsState.AppsMenuOpen => GoogleSheetsState.SheetsHome,
                    GoogleSheetsState.SheetsHome => GoogleSheetsState.BlankSpreadsheetOpen,
                    GoogleSheetsState.BlankSpreadsheetOpen => GoogleSheetsState.CreatedSpreadsheetTabClosed,
                    _ => state,
                };
        }
    }
}
