using System.Text.Json;

namespace Series4.Desktop;

public enum GoogleSheetsState
{
    Unknown,
    GoogleHome,
    AppsMenuOpen,
    SheetsHome,
    BlankSpreadsheetOpen,
    CreatedSpreadsheetTabClosed,
}

public sealed record DemonstrationInputEvent(int Index, double OffsetSeconds, string Kind);

public sealed record DemonstrationScreenEvidence(string Id, double OffsetSeconds, IReadOnlyList<string> VisibleSemantics);

public sealed record WorkflowEvidenceLink(
    string StepId,
    IReadOnlyList<int> EventIndices,
    IReadOnlyList<string> ScreenEvidenceIds);

public sealed record DemonstrationValidationResult(
    int EventCount,
    int CoveredEventCount,
    double Coverage,
    IReadOnlyList<int> MissingEventIndices);

public sealed record GoogleSheetsWorkflowStepDefinition(
    string Id,
    GoogleSheetsState From,
    GoogleSheetsState To,
    string Precondition,
    string SuccessCondition,
    WorkflowEvidenceLink Evidence);

public sealed record GoogleSheetsWorkflowDefinition(
    string NaturalLanguageIntent,
    DemonstrationValidationResult EvidenceCoverage,
    IReadOnlyList<GoogleSheetsWorkflowStepDefinition> Steps);

public static class GoogleSheetsWorkflowCompiler
{
    private static readonly (string Id, GoogleSheetsState From, GoogleSheetsState To, string Precondition, string Success)[] Template =
    [
        ("open-google-apps-menu", GoogleSheetsState.GoogleHome, GoogleSheetsState.AppsMenuOpen, "Chrome 활성 탭이 Google 기본 화면이고 Google 앱 버튼이 정확히 하나다.", "Google 앱 메뉴와 Sheets 항목이 함께 관찰된다."),
        ("open-sheets", GoogleSheetsState.AppsMenuOpen, GoogleSheetsState.SheetsHome, "Google 앱 메뉴에 Sheets 후보가 정확히 하나다.", "Sheets 홈 URL과 빈 스프레드시트 항목이 함께 관찰된다."),
        ("create-blank-spreadsheet", GoogleSheetsState.SheetsHome, GoogleSheetsState.BlankSpreadsheetOpen, "Sheets 홈에 빈 스프레드시트 후보가 정확히 하나다.", "문서 URL과 스프레드시트 그리드가 함께 관찰된다."),
        ("close-created-spreadsheet-tab", GoogleSheetsState.BlankSpreadsheetOpen, GoogleSheetsState.CreatedSpreadsheetTabClosed, "활성 탭이 방금 생성된 빈 스프레드시트로 검증된다.", "기록한 생성 탭 ID가 사라지고 다른 활성 탭이 관찰된다."),
    ];

    public static GoogleSheetsWorkflowDefinition Compile(
        string naturalLanguageIntent,
        double videoDurationSeconds,
        IReadOnlyList<DemonstrationInputEvent> events,
        IReadOnlyList<DemonstrationScreenEvidence> screens,
        IReadOnlyList<WorkflowEvidenceLink> links)
    {
        var intent = naturalLanguageIntent?.Trim() ?? string.Empty;
        if (intent.Length is < 3 or > 2000 || !(intent.Contains("스프레드시트", StringComparison.OrdinalIgnoreCase) || intent.Contains("sheet", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("자연어 의도에서 Google Sheets 빈 문서 생성 목표를 확인할 수 없습니다.");
        var expectedIds = Template.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var actualIds = links.Select(item => item.StepId).ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.SetEquals(actualIds) || links.Count != Template.Length)
            throw new InvalidOperationException("골든 패스의 모든 실행 단계가 녹화 근거와 연결되어야 합니다.");
        var coverage = DemonstrationEvidenceValidator.Validate(videoDurationSeconds, events, screens, links);
        var byId = links.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        return new GoogleSheetsWorkflowDefinition(intent, coverage, Template.Select(item =>
            new GoogleSheetsWorkflowStepDefinition(item.Id, item.From, item.To, item.Precondition, item.Success, byId[item.Id])).ToArray());
    }
}

public static class DemonstrationEvidenceValidator
{
    private static readonly HashSet<string> MeaningfulKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "MouseLeftClick", "MouseRightClick", "TextEntry", "KeyStroke", "MouseDrag", "MouseWheel",
    };

    public static DemonstrationValidationResult Validate(
        double videoDurationSeconds,
        IReadOnlyList<DemonstrationInputEvent> events,
        IReadOnlyList<DemonstrationScreenEvidence> screens,
        IReadOnlyList<WorkflowEvidenceLink> links,
        double maximumSynchronizationSkewSeconds = 1.0)
    {
        if (videoDurationSeconds <= 0 || !double.IsFinite(videoDurationSeconds))
            throw new InvalidOperationException("영상 길이가 유효하지 않습니다.");

        var meaningful = events.Where(item => MeaningfulKinds.Contains(item.Kind)).ToArray();
        if (meaningful.Length == 0)
            throw new InvalidOperationException("동기화된 입력 이벤트가 없어 Workflow를 생성할 수 없습니다.");
        if (screens.Count == 0)
            throw new InvalidOperationException("입력 이벤트와 대조할 화면 증거가 없습니다.");

        var duplicateScreenIds = screens.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key).ToArray();
        if (duplicateScreenIds.Length > 0 || screens.Any(item => string.IsNullOrWhiteSpace(item.Id)))
            throw new InvalidOperationException("화면 증거 ID가 비어 있거나 중복되었습니다.");
        foreach (var screen in screens)
        {
            if (!double.IsFinite(screen.OffsetSeconds) || screen.OffsetSeconds < 0 || screen.OffsetSeconds > videoDurationSeconds)
                throw new InvalidOperationException($"화면 증거 {screen.Id}의 시간이 영상 범위를 벗어났습니다.");
            if (!screen.VisibleSemantics.Any(value => !string.IsNullOrWhiteSpace(value)))
                throw new InvalidOperationException($"화면 증거 {screen.Id}에 검증 가능한 의미 정보가 없습니다.");
        }

        var duplicateIndices = meaningful.GroupBy(item => item.Index).Where(group => group.Count() != 1).Select(group => group.Key).ToArray();
        if (duplicateIndices.Length > 0)
            throw new InvalidOperationException($"입력 이벤트 인덱스가 중복되었습니다: {string.Join(", ", duplicateIndices)}");

        foreach (var input in meaningful)
        {
            if (!double.IsFinite(input.OffsetSeconds) || input.OffsetSeconds < 0 || input.OffsetSeconds > videoDurationSeconds)
                throw new InvalidOperationException($"입력 이벤트 {input.Index}의 시간이 영상 범위를 벗어났습니다.");
            var nearest = screens.Min(screen => Math.Abs(screen.OffsetSeconds - input.OffsetSeconds));
            if (nearest > maximumSynchronizationSkewSeconds)
                throw new InvalidOperationException($"입력 이벤트 {input.Index}와 영상 화면의 시간 차이가 {nearest:0.###}초입니다.");
        }

        var eventIndices = meaningful.Select(item => item.Index).ToHashSet();
        var screenIds = screens.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (links.Count == 0 || links.Select(link => link.StepId).Distinct(StringComparer.Ordinal).Count() != links.Count)
            throw new InvalidOperationException("실행 단계 근거 연결이 없거나 단계 ID가 중복되었습니다.");
        var covered = new HashSet<int>();
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.StepId) || link.EventIndices.Count == 0 || link.ScreenEvidenceIds.Count == 0)
                throw new InvalidOperationException("모든 실행 단계는 입력 이벤트와 화면 증거를 모두 참조해야 합니다.");
            var unknownEvents = link.EventIndices.Where(index => !eventIndices.Contains(index)).ToArray();
            var unknownScreens = link.ScreenEvidenceIds.Where(id => !screenIds.Contains(id)).ToArray();
            if (unknownEvents.Length > 0 || unknownScreens.Length > 0)
                throw new InvalidOperationException($"단계 {link.StepId}가 존재하지 않는 녹화 근거를 참조합니다.");
            covered.UnionWith(link.EventIndices);
        }

        var coverage = (double)covered.Count / eventIndices.Count;
        var missing = eventIndices.Except(covered).Order().ToArray();
        if (coverage < 0.8)
            throw new InvalidOperationException($"행동 증거 반영률이 80% 미만입니다: {coverage:P0}; 누락={string.Join(",", missing)}");
        return new DemonstrationValidationResult(eventIndices.Count, covered.Count, coverage, missing);
    }
}

public sealed record SemanticElement(
    string Id,
    string Role,
    string Name,
    bool Enabled = true,
    bool Offscreen = false,
    bool Password = false);

public sealed record BrowserTab(string Id, string Title, string Url, bool Active);

public sealed record GoogleSheetsObservation(
    string ProcessName,
    IReadOnlyList<BrowserTab> Tabs,
    IReadOnlyList<SemanticElement> Elements,
    long Revision)
{
    public BrowserTab? ActiveTab
    {
        get
        {
            var active = Tabs.Where(tab => tab.Active).Take(2).ToArray();
            return active.Length == 1 ? active[0] : null;
        }
    }
}

public sealed record GoldenPathAction(string StepId, string Kind, string? TargetId, string Description);

public interface IGoogleSheetsSurface
{
    GoogleSheetsObservation Observe();
    void Execute(GoldenPathAction action, GoogleSheetsObservation observation);
}

public sealed record GoldenPathJournalEntry(
    DateTimeOffset At,
    string StepId,
    string Phase,
    GoogleSheetsState ObservedState,
    string Observation,
    IReadOnlyList<string> Candidates,
    string Decision,
    string Execution,
    string Verification);

public sealed record GoldenPathRunResult(string Status, GoogleSheetsState State, IReadOnlyList<GoldenPathJournalEntry> Journal)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class GoogleSheetsGoldenPathRunner
{
    private const string GoogleHomeStep = "open-google-apps-menu";
    private const string SheetsHomeStep = "open-sheets";
    private const string BlankSheetStep = "create-blank-spreadsheet";
    private const string CloseTabStep = "close-created-spreadsheet-tab";

    public GoldenPathRunResult Run(
        IGoogleSheetsSurface surface,
        int maximumTransitions = 8,
        int verificationAttempts = 10,
        TimeSpan? verificationDelay = null)
    {
        if (verificationAttempts is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(verificationAttempts));
        var delay = verificationDelay ?? TimeSpan.FromMilliseconds(250);
        var journal = new List<GoldenPathJournalEntry>();
        string? sourceTabId = null;
        string? createdTabId = null;

        for (var transition = 0; transition < maximumTransitions; transition++)
        {
            var before = surface.Observe();
            var classified = Classify(before, sourceTabId, createdTabId);
            if (!IsChrome(before))
                return Stop("ABSTAINED", classified, "foreground process is not Chrome", before, [], journal);

            if (classified == GoogleSheetsState.CreatedSpreadsheetTabClosed)
                return new GoldenPathRunResult("SUCCESS", classified, journal);

            var active = before.ActiveTab;
            if (active is null)
                return Stop("ABSTAINED", classified, "active Chrome tab is not uniquely observable", before, [], journal);

            GoldenPathAction? action = null;
            IReadOnlyList<SemanticElement> candidates = [];
            string stepId;
            switch (classified)
            {
                case GoogleSheetsState.GoogleHome:
                    sourceTabId ??= active.Id;
                    stepId = GoogleHomeStep;
                    candidates = Exact(before, new[] { "button" }, new[] { "Google apps", "Google 앱" });
                    action = UniqueClick(stepId, "Google 앱 메뉴 열기", candidates);
                    break;
                case GoogleSheetsState.AppsMenuOpen:
                    sourceTabId ??= active.Id;
                    stepId = SheetsHomeStep;
                    candidates = Exact(before, new[] { "link", "hyperlink", "menuitem", "button" }, new[] { "Sheets", "스프레드시트", "Google Sheets" });
                    action = UniqueClick(stepId, "Sheets 열기", candidates);
                    break;
                case GoogleSheetsState.SheetsHome:
                    sourceTabId ??= active.Id;
                    stepId = BlankSheetStep;
                    candidates = Exact(before, new[] { "button", "link", "hyperlink", "listitem", "custom" }, new[] { "Blank spreadsheet", "빈 스프레드시트", "Blank" });
                    action = UniqueClick(stepId, "빈 스프레드시트 만들기", candidates);
                    break;
                case GoogleSheetsState.BlankSpreadsheetOpen:
                    createdTabId ??= active.Id;
                    stepId = CloseTabStep;
                    action = new GoldenPathAction(stepId, "close_active_tab", null, "생성된 스프레드시트 탭 닫기");
                    break;
                default:
                    return Stop("ABSTAINED", classified, "semantic state cannot be verified", before, [], journal);
            }

            if (action is null)
            {
                var reason = candidates.Count == 0 ? "no exact semantic target" : "multiple exact semantic targets";
                return Stop("ABSTAINED", classified, reason, before, candidates, journal, stepId);
            }

            var candidateIds = candidates.Select(item => item.Id).ToArray();
            journal.Add(Entry(stepId, "decision", classified, before, candidateIds, action.Description, "pending", "pending"));
            try
            {
                surface.Execute(action, before);
            }
            catch (Exception error)
            {
                journal.Add(Entry(stepId, "execution", classified, before, candidateIds, action.Description, error.Message, "not-run"));
                return new GoldenPathRunResult("EXECUTION_FAILED", classified, journal);
            }

            var expected = Successor(classified);
            var after = surface.Observe();
            var nextState = Classify(after, sourceTabId, createdTabId);
            var observationAttempt = 1;
            while (nextState != expected && observationAttempt < verificationAttempts)
            {
                if (delay > TimeSpan.Zero) Thread.Sleep(delay);
                after = surface.Observe();
                nextState = Classify(after, sourceTabId, createdTabId);
                observationAttempt++;
            }
            var verified = expected == nextState;
            journal.Add(Entry(stepId, "verification", nextState, after, candidateIds, action.Description, "executed", verified ? $"success after {observationAttempt} observation(s)" : $"expected {expected}, observed {nextState} after {observationAttempt} observation(s)"));
            if (!verified)
                return new GoldenPathRunResult("VERIFICATION_FAILED", nextState, journal);
        }
        var last = surface.Observe();
        return Stop("TRANSITION_LIMIT", Classify(last, sourceTabId, createdTabId), "transition budget exhausted", last, [], journal);
    }

    public static GoogleSheetsState Classify(GoogleSheetsObservation observation, string? sourceTabId = null, string? createdTabId = null)
    {
        if (!IsChrome(observation) || observation.ActiveTab is not { } active) return GoogleSheetsState.Unknown;
        var createdStillOpen = createdTabId is not null && observation.Tabs.Any(tab => tab.Id == createdTabId);
        if (createdTabId is not null && !createdStillOpen && observation.Tabs.Any(tab => tab.Active))
            return GoogleSheetsState.CreatedSpreadsheetTabClosed;
        if (IsSpreadsheetEdit(active) && HasAny(observation, "grid", "textbox", "formula bar", "수식 입력줄"))
            return GoogleSheetsState.BlankSpreadsheetOpen;
        if (IsSheetsHome(active) && HasExact(observation, "Blank spreadsheet", "빈 스프레드시트", "Blank"))
            return GoogleSheetsState.SheetsHome;
        if (IsGoogleHome(active) && HasExact(observation, "Sheets", "스프레드시트", "Google Sheets") && HasAny(observation, "menu", "dialog", "Google apps", "Google 앱"))
            return GoogleSheetsState.AppsMenuOpen;
        if (IsGoogleHome(active) && HasExact(observation, "Google apps", "Google 앱"))
            return GoogleSheetsState.GoogleHome;
        return GoogleSheetsState.Unknown;
    }

    private static bool IsChrome(GoogleSheetsObservation value) => value.ProcessName.Contains("chrome", StringComparison.OrdinalIgnoreCase);
    private static bool IsGoogleHome(BrowserTab tab) => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host is "www.google.com" or "google.com";
    private static bool IsSheetsHome(BrowserTab tab) => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host == "docs.google.com" && uri.AbsolutePath.StartsWith("/spreadsheets", StringComparison.Ordinal) && !uri.AbsolutePath.Contains("/d/", StringComparison.Ordinal) && !uri.AbsolutePath.Contains("/create", StringComparison.Ordinal);
    private static bool IsSpreadsheetEdit(BrowserTab tab) => Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host == "docs.google.com" && uri.AbsolutePath.Contains("/spreadsheets/d/", StringComparison.Ordinal);
    private static bool HasExact(GoogleSheetsObservation observation, params string[] names) => Exact(observation, ["button", "link", "hyperlink", "menuitem", "listitem", "custom"], names).Count > 0;
    private static bool HasAny(GoogleSheetsObservation observation, params string[] values) => observation.Elements.Any(item => values.Any(value => item.Role.Contains(value, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<SemanticElement> Exact(GoogleSheetsObservation observation, IReadOnlyList<string> roles, IReadOnlyList<string> names) =>
        observation.Elements.Where(element => element.Enabled && !element.Offscreen && !element.Password && roles.Any(role => string.Equals(role, element.Role, StringComparison.OrdinalIgnoreCase)) && names.Any(name => string.Equals(name, element.Name, StringComparison.OrdinalIgnoreCase))).ToArray();

    private static GoldenPathAction? UniqueClick(string stepId, string description, IReadOnlyList<SemanticElement> candidates) =>
        candidates.Count == 1 ? new GoldenPathAction(stepId, "click", candidates[0].Id, description) : null;

    private static GoogleSheetsState Successor(GoogleSheetsState state) => state switch
    {
        GoogleSheetsState.GoogleHome => GoogleSheetsState.AppsMenuOpen,
        GoogleSheetsState.AppsMenuOpen => GoogleSheetsState.SheetsHome,
        GoogleSheetsState.SheetsHome => GoogleSheetsState.BlankSpreadsheetOpen,
        GoogleSheetsState.BlankSpreadsheetOpen => GoogleSheetsState.CreatedSpreadsheetTabClosed,
        _ => GoogleSheetsState.Unknown,
    };

    private static GoldenPathRunResult Stop(string status, GoogleSheetsState state, string reason, GoogleSheetsObservation observation, IReadOnlyList<SemanticElement> candidates, List<GoldenPathJournalEntry> journal, string stepId = "state-detection")
    {
        journal.Add(Entry(stepId, "abstain", state, observation, candidates.Select(item => item.Id).ToArray(), reason, "not-run", "not-run"));
        return new GoldenPathRunResult(status, state, journal);
    }

    private static GoldenPathJournalEntry Entry(string stepId, string phase, GoogleSheetsState state, GoogleSheetsObservation observation, IReadOnlyList<string> candidates, string decision, string execution, string verification) =>
        new(DateTimeOffset.UtcNow, stepId, phase, state, $"revision={observation.Revision}; active={observation.ActiveTab?.Title}; url={observation.ActiveTab?.Url}", candidates, decision, execution, verification);
}
