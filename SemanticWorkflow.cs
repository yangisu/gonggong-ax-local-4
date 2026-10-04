using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Series4.Desktop;

public sealed record SemanticTargetSelector(
    IReadOnlyList<string> Roles,
    string Name,
    string? AutomationId = null,
    bool AllowOffscreen = false,
    string? ExpectedValue = null,
    bool RequireKeyboardFocus = false);

public sealed record SemanticElementEvidence(
    string Role,
    string Name,
    string AutomationId = "",
    string Value = "",
    bool Enabled = true,
    bool Offscreen = false,
    bool Password = false,
    bool KeyboardFocused = false);

public sealed record SemanticDemonstrationFrame(
    string Id,
    double OffsetSeconds,
    string ProcessName,
    string WindowTitle,
    string Url,
    IReadOnlyList<SemanticElementEvidence> Elements,
    string? VideoFrameSha256 = null);

public sealed record SemanticDemonstrationAction(
    string Id,
    int EventIndex,
    double OffsetSeconds,
    string Kind,
    SemanticTargetSelector? Target,
    string? Value,
    string BeforeFrameId,
    string AfterFrameId,
    IReadOnlyList<int>? AdditionalEventIndices = null,
    IReadOnlyList<string>? AdditionalScreenEvidenceIds = null);

public sealed record SemanticDemonstration(
    string NaturalLanguageIntent,
    string VideoPath,
    double VideoDurationSeconds,
    IReadOnlyList<DemonstrationInputEvent> InputEvents,
    IReadOnlyList<SemanticDemonstrationFrame> Frames,
    IReadOnlyList<SemanticDemonstrationAction> Actions);

public sealed record SemanticStatePredicate(
    string ProcessName,
    string? WindowTitle,
    string? UrlOrigin,
    string? UrlPathPrefix,
    IReadOnlyList<SemanticTargetSelector> RequiredElements)
{
    public bool Matches(SemanticWorkflowObservation observation)
    {
        if (!Same(ProcessName, observation.ProcessName)) return false;
        if (!string.IsNullOrWhiteSpace(WindowTitle)
            && !string.Equals(WindowTitle.Trim(), observation.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!MatchesUrl(observation.Url)) return false;
        return RequiredElements.All(selector => SemanticWorkflowMatching.Find(observation, selector).Count == 1);
    }

    private bool MatchesUrl(string candidate)
    {
        if (string.IsNullOrWhiteSpace(UrlOrigin) && string.IsNullOrWhiteSpace(UrlPathPrefix)) return true;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        var origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        return (string.IsNullOrWhiteSpace(UrlOrigin) || string.Equals(origin, UrlOrigin, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(UrlPathPrefix) || uri.AbsolutePath.StartsWith(UrlPathPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Same(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

public sealed record SemanticWorkflowEvidence(
    IReadOnlyList<int> EventIndices,
    string BeforeFrameId,
    string AfterFrameId,
    IReadOnlyList<string> ScreenEvidenceIds);

public sealed record SemanticWorkflowActionDefinition(
    string Kind,
    SemanticTargetSelector? Target,
    string? Value);

public sealed record SemanticScrollCommand(string Axis, string Direction, int Count)
{
    public override string ToString() => $"{Axis}:{Direction}:{Count}";

    public static bool TryParse(string? value, out SemanticScrollCommand command)
    {
        command = new SemanticScrollCommand(string.Empty, string.Empty, 0);
        var parts = value?.Split(':', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length != 3
            || parts[0] is not ("horizontal" or "vertical")
            || parts[1] is not ("increment" or "decrement")
            || !int.TryParse(parts[2], out var count)
            || count is < 1 or > 20)
            return false;
        command = new SemanticScrollCommand(parts[0], parts[1], count);
        return true;
    }
}

public static class SemanticRangeValue
{
    public static bool TryParse(string? value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

public static class SemanticOrdinalValue
{
    public static bool TryParse(string? value, out int ordinal)
    {
        var candidate = value?.StartsWith("ordinal=", StringComparison.OrdinalIgnoreCase) == true
            ? value[8..]
            : value;
        return int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            && ordinal is >= 0 and <= 9999;
    }

    public static string Format(int ordinal) => ordinal.ToString(CultureInfo.InvariantCulture);

    public static string FormatEvidence(int ordinal) => $"ordinal={Format(ordinal)}";
}

public sealed record SemanticWorkflowStepDefinition(
    string Id,
    string FromStateId,
    string ToStateId,
    SemanticStatePredicate Precondition,
    SemanticStatePredicate SuccessCondition,
    SemanticWorkflowActionDefinition Action,
    SemanticWorkflowEvidence Evidence);

public sealed record SemanticVideoEvidenceReference(
    string ScreenEvidenceId,
    double OffsetSeconds,
    string VideoFrameSha256);

public sealed record SemanticVideoEvidenceValidationResult(
    int ReferencedFrameCount,
    int HashedFrameCount,
    double Coverage);

public sealed record SemanticWorkflowDefinition(
    int Version,
    string NaturalLanguageIntent,
    string SourceVideoSha256,
    string SourceVideoPath,
    DemonstrationValidationResult EvidenceCoverage,
    SemanticVideoEvidenceValidationResult VideoEvidenceCoverage,
    IReadOnlyList<SemanticVideoEvidenceReference> VideoEvidence,
    IReadOnlyList<SemanticWorkflowStepDefinition> Steps)
{
    public string ToJson() => JsonSerializer.Serialize(this, SemanticWorkflowJson.Options);

    public static SemanticWorkflowDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<SemanticWorkflowDefinition>(json, SemanticWorkflowJson.Options)
        ?? throw new InvalidDataException("의미 Workflow JSON이 비어 있습니다.");
}

public static class SemanticWorkflowJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class SemanticWorkflowIntegrityVerifier
{
    public static void VerifySourceVideo(SemanticWorkflowDefinition workflow, string? sourceVideoOverride = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var path = string.IsNullOrWhiteSpace(sourceVideoOverride)
            ? workflow.SourceVideoPath
            : Path.GetFullPath(sourceVideoOverride);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Workflow의 원본 시연 영상을 찾을 수 없습니다.");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, workflow.SourceVideoSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("원본 시연 영상 SHA-256이 Workflow 생성 시점과 달라 실행할 수 없습니다.");
    }
}

public static class SemanticWorkflowCompiler
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "click", "type", "select", "select-option", "toggle", "scroll", "set-range", "focus", "reorder-item",
    };

    private static readonly string[] IrreversibleTerms =
    [
        "delete", "remove", "erase", "purchase", "pay", "buy", "submit payment",
        "삭제", "제거", "결제", "구매", "송금", "탈퇴",
    ];

    public static SemanticWorkflowDefinition Compile(SemanticDemonstration demonstration)
    {
        ArgumentNullException.ThrowIfNull(demonstration);
        var intent = demonstration.NaturalLanguageIntent?.Trim() ?? string.Empty;
        if (intent.Length is < 3 or > 2000)
            throw new InvalidOperationException("자동화 의도는 3~2000자로 입력해야 합니다.");
        if (IrreversibleTerms.Any(term => intent.Contains(term, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("삭제·결제 등 비가역 작업은 무인 Workflow로 만들 수 없습니다.");
        if (string.IsNullOrWhiteSpace(demonstration.VideoPath) || !File.Exists(demonstration.VideoPath))
            throw new InvalidOperationException("원본 시연 영상 파일이 없습니다.");
        var video = new FileInfo(demonstration.VideoPath);
        if (video.Length is < 1 or > 500 * 1024 * 1024)
            throw new InvalidOperationException("원본 시연 영상 크기가 허용 범위를 벗어났습니다.");
        ValidateMp4(demonstration.VideoPath);
        if (demonstration.Actions.Count == 0)
            throw new InvalidOperationException("의미 Workflow로 만들 실행 동작이 없습니다.");

        var frameById = demonstration.Frames
            .GroupBy(frame => frame.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (frameById.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value.Length != 1))
            throw new InvalidOperationException("시연 화면 ID가 비어 있거나 중복되었습니다.");

        var links = new List<WorkflowEvidenceLink>();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < demonstration.Actions.Count; index++)
        {
            var action = demonstration.Actions[index];
            if (string.IsNullOrWhiteSpace(action.Id) || !stepIds.Add(action.Id))
                throw new InvalidOperationException("Workflow 단계 ID가 비어 있거나 중복되었습니다.");
            if (!SupportedActions.Contains(action.Kind))
                throw new InvalidOperationException($"지원하지 않는 의미 동작입니다: {action.Kind}");
            if (action.Target is null)
                throw new InvalidOperationException($"단계 {action.Id}의 의미 대상이 없습니다.");
            ValidateSelector(action.Target, action.Id);
            if (action.Kind is "type" or "select" or "select-option" && string.IsNullOrEmpty(action.Value))
                throw new InvalidOperationException($"단계 {action.Id}에 입력 값이 없습니다.");
            if (action.Kind == "scroll" && !SemanticScrollCommand.TryParse(action.Value, out _))
                throw new InvalidOperationException($"단계 {action.Id}의 스크롤 값이 올바르지 않습니다.");
            if (action.Kind == "set-range" && !SemanticRangeValue.TryParse(action.Value, out _))
                throw new InvalidOperationException($"단계 {action.Id}의 범위 조절 값이 올바르지 않습니다.");
            if (action.Kind == "reorder-item" && !SemanticOrdinalValue.TryParse(action.Value, out _))
                throw new InvalidOperationException($"단계 {action.Id}의 목록 순서 값이 올바르지 않습니다.");
            if (!frameById.TryGetValue(action.BeforeFrameId, out var beforeItems)
                || !frameById.TryGetValue(action.AfterFrameId, out var afterItems))
                throw new InvalidOperationException($"단계 {action.Id}가 존재하지 않는 화면 증거를 참조합니다.");
            var before = beforeItems[0];
            var after = afterItems[0];
            if (before.OffsetSeconds > action.OffsetSeconds || after.OffsetSeconds < action.OffsetSeconds)
                throw new InvalidOperationException($"단계 {action.Id}의 전후 화면과 입력 이벤트 순서가 맞지 않습니다.");
            if (index > 0 && demonstration.Actions[index - 1].AfterFrameId != action.BeforeFrameId)
                throw new InvalidOperationException("시연 단계의 전후 상태 연결이 끊어졌습니다.");
            var matches = before.Elements.Count(element => Matches(element, action.Target));
            if (matches != 1)
                throw new InvalidOperationException($"단계 {action.Id}의 시연 대상이 전 화면에서 정확히 하나가 아닙니다: {matches}개");
            var target = before.Elements.Single(element => Matches(element, action.Target));
            if (!target.Enabled || target.Password)
                throw new InvalidOperationException($"단계 {action.Id}의 대상은 비활성 또는 비밀번호 필드입니다.");
            var eventIndices = new[] { action.EventIndex }
                .Concat(action.AdditionalEventIndices ?? [])
                .Distinct()
                .ToArray();
            var screenIds = new[] { action.BeforeFrameId, action.AfterFrameId }
                .Concat(action.AdditionalScreenEvidenceIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            links.Add(new WorkflowEvidenceLink(action.Id, eventIndices, screenIds));
        }

        var screens = demonstration.Frames.Select(frame => new DemonstrationScreenEvidence(
            frame.Id,
            frame.OffsetSeconds,
            frame.Elements.Select(element => $"{element.Role}:{element.Name}")
                .Append(frame.ProcessName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray())).ToArray();
        var coverage = DemonstrationEvidenceValidator.Validate(
            demonstration.VideoDurationSeconds,
            demonstration.InputEvents,
            screens,
            links);
        var referencedScreenIds = links.SelectMany(link => link.ScreenEvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var referencedFrames = referencedScreenIds.Select(id => frameById[id][0]).ToArray();
        var hashedFrames = referencedFrames.Where(frame => IsSha256(frame.VideoFrameSha256)).ToArray();
        var videoCoverage = (double)hashedFrames.Length / referencedFrames.Length;
        if (videoCoverage < 0.8)
            throw new InvalidOperationException($"단계별 MP4 프레임 증거 반영률이 80% 미만입니다: {videoCoverage:P0}");
        var videoEvidence = hashedFrames.Select(frame => new SemanticVideoEvidenceReference(
            frame.Id,
            frame.OffsetSeconds,
            frame.VideoFrameSha256!)).ToArray();

        var steps = new List<SemanticWorkflowStepDefinition>();
        for (var index = 0; index < demonstration.Actions.Count; index++)
        {
            var action = demonstration.Actions[index];
            var before = frameById[action.BeforeFrameId][0];
            var after = frameById[action.AfterFrameId][0];
            var precondition = BuildPredicate(before, previous: null, requiredTarget: action.Target);
            var nextTarget = index + 1 < demonstration.Actions.Count ? demonstration.Actions[index + 1].Target : null;
            var successTarget = action.Kind.Equals("reorder-item", StringComparison.OrdinalIgnoreCase)
                && SemanticOrdinalValue.TryParse(action.Value, out var finalOrdinal)
                    ? action.Target! with { ExpectedValue = SemanticOrdinalValue.FormatEvidence(finalOrdinal) }
                    : nextTarget;
            var success = BuildPredicate(
                after,
                before,
                successTarget,
                action.Kind.Equals("scroll", StringComparison.OrdinalIgnoreCase) ? action.Target : null);
            if (!Distinguishes(success, before))
                throw new InvalidOperationException($"단계 {action.Id} 이후의 의미 상태를 이전 상태와 구분할 근거가 없습니다.");
            steps.Add(new SemanticWorkflowStepDefinition(
                action.Id,
                $"state-{index}",
                $"state-{index + 1}",
                precondition,
                success,
                new SemanticWorkflowActionDefinition(action.Kind.ToLowerInvariant(), action.Target, action.Value),
                new SemanticWorkflowEvidence(
                    new[] { action.EventIndex }.Concat(action.AdditionalEventIndices ?? []).Distinct().ToArray(),
                    action.BeforeFrameId,
                    action.AfterFrameId,
                    new[] { action.BeforeFrameId, action.AfterFrameId }
                        .Concat(action.AdditionalScreenEvidenceIds ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray())));
        }

        string hash;
        using (var stream = File.OpenRead(demonstration.VideoPath))
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new SemanticWorkflowDefinition(
            2,
            intent,
            hash,
            Path.GetFullPath(demonstration.VideoPath),
            coverage,
            new SemanticVideoEvidenceValidationResult(referencedFrames.Length, hashedFrames.Length, videoCoverage),
            videoEvidence,
            steps);
    }

    private static void ValidateMp4(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length
            || header[4] != (byte)'f'
            || header[5] != (byte)'t'
            || header[6] != (byte)'y'
            || header[7] != (byte)'p')
            throw new InvalidOperationException("원본 시연 파일이 MP4 컨테이너가 아닙니다.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static SemanticStatePredicate BuildPredicate(
        SemanticDemonstrationFrame frame,
        SemanticDemonstrationFrame? previous,
        SemanticTargetSelector? requiredTarget,
        SemanticTargetSelector? ignoredChangedTarget = null)
    {
        var required = new List<SemanticTargetSelector>();
        if (requiredTarget is not null) required.Add(requiredTarget);
        if (previous is not null)
        {
            var previousIdentities = previous.Elements.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            required.AddRange(frame.Elements
                .Where(element => element.Enabled && !element.Offscreen && !element.Password && !string.IsNullOrWhiteSpace(element.Name))
                .Where(element => !previousIdentities.Contains(Identity(element)))
                .Where(element => ignoredChangedTarget is null || !Matches(element, ignoredChangedTarget))
                .Where(element => !required.Any(selector => Matches(element, selector)))
                .OrderByDescending(element => !string.IsNullOrWhiteSpace(element.AutomationId))
                .Take(3)
                .Select(ToSelector));
        }

        var (origin, path) = UrlScope(frame.Url, previous?.Url);
        var title = previous is not null
            && !string.Equals(frame.WindowTitle.Trim(), previous.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(frame.WindowTitle)
                ? frame.WindowTitle.Trim()
                : null;
        return new SemanticStatePredicate(frame.ProcessName, title, origin, path, required);
    }

    private static bool Distinguishes(SemanticStatePredicate predicate, SemanticDemonstrationFrame previous)
    {
        var observation = new SemanticWorkflowObservation(
            previous.ProcessName,
            previous.WindowTitle,
            previous.Url,
            previous.Elements.Select((element, index) => new SemanticWorkflowElement(
                $"previous-{index}", element.Role, element.Name, element.AutomationId, element.Value,
                element.Enabled, element.Offscreen, element.Password, element.KeyboardFocused)).ToArray(), 0);
        return !predicate.Matches(observation);
    }

    private static (string? Origin, string? Path) UrlScope(string current, string? previous)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)) return (null, null);
        if (previous is not null && string.Equals(current, previous, StringComparison.OrdinalIgnoreCase)) return (null, null);
        var origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stable = segments.TakeWhile(segment => segment.Length <= 32 && !LooksOpaque(segment)).ToArray();
        var prefix = stable.Length == 0 ? "/" : "/" + string.Join('/', stable) + "/";
        return (origin, prefix);
    }

    private static bool LooksOpaque(string segment) =>
        segment.Length >= 12 && segment.Count(char.IsLetterOrDigit) >= 10;

    private static void ValidateSelector(SemanticTargetSelector selector, string stepId)
    {
        if (selector.Roles.Count == 0 || selector.Roles.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(selector.Name))
            throw new InvalidOperationException($"단계 {stepId}의 의미 대상 역할 또는 이름이 비어 있습니다.");
        if (IrreversibleTerms.Any(term => selector.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"단계 {stepId}의 비가역 대상은 무인 실행할 수 없습니다: {selector.Name}");
    }

    private static SemanticTargetSelector ToSelector(SemanticElementEvidence element) =>
        new(
            [element.Role],
            element.Name,
            string.IsNullOrWhiteSpace(element.AutomationId) ? null : element.AutomationId,
            ExpectedValue: string.IsNullOrEmpty(element.Value) ? null : element.Value,
            RequireKeyboardFocus: element.KeyboardFocused);

    private static string Identity(SemanticElementEvidence element) =>
        $"{element.Role}\u001f{element.Name}\u001f{element.AutomationId}\u001f{element.Value}\u001f{element.KeyboardFocused}";

    private static bool Matches(SemanticElementEvidence element, SemanticTargetSelector selector) =>
        selector.Roles.Any(role => string.Equals(role, element.Role, StringComparison.OrdinalIgnoreCase))
        && string.Equals(selector.Name.Trim(), element.Name.Trim(), StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(selector.AutomationId)
            || string.Equals(selector.AutomationId, element.AutomationId, StringComparison.Ordinal))
        && (selector.ExpectedValue is null
            || string.Equals(selector.ExpectedValue, element.Value, StringComparison.Ordinal))
        && (!selector.RequireKeyboardFocus || element.KeyboardFocused);
}

public sealed record SemanticWorkflowElement(
    string Id,
    string Role,
    string Name,
    string AutomationId,
    string Value,
    bool Enabled,
    bool Offscreen,
    bool Password,
    bool KeyboardFocused = false);

public sealed record SemanticWorkflowObservation(
    string ProcessName,
    string WindowTitle,
    string Url,
    IReadOnlyList<SemanticWorkflowElement> Elements,
    long Revision);

public static class SemanticWorkflowMatching
{
    public static IReadOnlyList<SemanticWorkflowElement> Find(
        SemanticWorkflowObservation observation,
        SemanticTargetSelector selector) => observation.Elements.Where(element =>
            element.Enabled
            && !element.Password
            && (selector.AllowOffscreen || !element.Offscreen)
            && selector.Roles.Any(role => string.Equals(role, element.Role, StringComparison.OrdinalIgnoreCase))
            && string.Equals(selector.Name.Trim(), element.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(selector.AutomationId)
                || string.Equals(selector.AutomationId, element.AutomationId, StringComparison.Ordinal))
            && (selector.ExpectedValue is null
                || string.Equals(selector.ExpectedValue, element.Value, StringComparison.Ordinal))
            && (!selector.RequireKeyboardFocus || element.KeyboardFocused)).ToArray();
}

public sealed record SemanticPlannedAction(
    string StepId,
    string Kind,
    string TargetId,
    SemanticTargetSelector Target,
    string? Value);

public interface ISemanticWorkflowSurface
{
    SemanticWorkflowObservation Observe();
    void Execute(SemanticPlannedAction action, SemanticWorkflowObservation observation);
}

public sealed record SemanticWorkflowJournalEntry(
    DateTimeOffset At,
    string StepId,
    string Phase,
    string ObservedState,
    string Observation,
    IReadOnlyList<string> Candidates,
    string Decision,
    string Execution,
    string Verification);

public sealed record SemanticWorkflowRunResult(
    string Status,
    string StateId,
    IReadOnlyList<SemanticWorkflowJournalEntry> Journal)
{
    public string ToJson() => JsonSerializer.Serialize(this, SemanticWorkflowJson.Options);
}

public sealed class SemanticWorkflowRunner
{
    public SemanticWorkflowRunResult Run(
        SemanticWorkflowDefinition workflow,
        ISemanticWorkflowSurface surface,
        int verificationAttempts = 10,
        TimeSpan? verificationDelay = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(surface);
        if (workflow.Steps.Count == 0) throw new ArgumentException("Workflow 단계가 없습니다.", nameof(workflow));
        if (verificationAttempts is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(verificationAttempts));
        var delay = verificationDelay ?? TimeSpan.FromMilliseconds(250);
        var journal = new List<SemanticWorkflowJournalEntry>();

        for (var transition = 0; transition <= workflow.Steps.Count; transition++)
        {
            SemanticWorkflowObservation before;
            try { before = surface.Observe(); }
            catch (Exception error)
            {
                journal.Add(Failure("state-detection", error));
                return new SemanticWorkflowRunResult("OBSERVATION_FAILED", "unknown", journal);
            }

            var state = DetectState(workflow, before);
            if (state.Index < 0)
                return Stop("ABSTAINED", "unknown", state.Reason, before, [], journal);
            if (state.Index == workflow.Steps.Count)
                return new SemanticWorkflowRunResult("SUCCESS", $"state-{state.Index}", journal);

            var step = workflow.Steps[state.Index];
            var candidates = SemanticWorkflowMatching.Find(before, step.Action.Target!);
            if (candidates.Count != 1)
                return Stop("ABSTAINED", step.FromStateId,
                    candidates.Count == 0 ? "no exact semantic target" : "multiple exact semantic targets",
                    before, candidates, journal, step.Id);
            var planned = new SemanticPlannedAction(step.Id, step.Action.Kind, candidates[0].Id, step.Action.Target!, step.Action.Value);
            journal.Add(Entry(step.Id, "decision", step.FromStateId, before, candidates,
                $"execute {planned.Kind} on unique semantic target", "pending", "pending"));
            try { surface.Execute(planned, before); }
            catch (Exception error)
            {
                journal.Add(Entry(step.Id, "execution", step.FromStateId, before, candidates,
                    "target re-resolution required", error.Message, "not-run"));
                return new SemanticWorkflowRunResult("EXECUTION_FAILED", step.FromStateId, journal);
            }

            SemanticWorkflowObservation after = before;
            var verified = false;
            var attempt = 0;
            for (; attempt < verificationAttempts; attempt++)
            {
                if (attempt > 0 && delay > TimeSpan.Zero) Thread.Sleep(delay);
                try { after = surface.Observe(); }
                catch (Exception error)
                {
                    journal.Add(Failure(step.Id, error));
                    return new SemanticWorkflowRunResult("OBSERVATION_FAILED", step.FromStateId, journal);
                }
                if (step.SuccessCondition.Matches(after))
                {
                    var detected = DetectState(workflow, after);
                    verified = detected.Index == state.Index + 1;
                    if (verified) break;
                }
            }
            journal.Add(Entry(step.Id, "verification", step.ToStateId, after, candidates,
                planned.Kind, "executed", verified
                    ? $"success after {attempt + 1} observation(s)"
                    : $"expected unique {step.ToStateId} after {verificationAttempts} observation(s)"));
            if (!verified)
                return new SemanticWorkflowRunResult("VERIFICATION_FAILED", step.FromStateId, journal);
        }
        return new SemanticWorkflowRunResult("TRANSITION_LIMIT", "unknown", journal);
    }

    private static (int Index, string Reason) DetectState(
        SemanticWorkflowDefinition workflow,
        SemanticWorkflowObservation observation)
    {
        var predicates = workflow.Steps.Select(step => step.Precondition)
            .Append(workflow.Steps[^1].SuccessCondition)
            .ToArray();
        var matches = predicates.Select((predicate, index) => (predicate, index))
            .Where(item => item.predicate.Matches(observation))
            .Select(item => item.index)
            .ToArray();
        if (matches.Length == 0) return (-1, "semantic state cannot be verified");
        if (matches.Length == 1) return (matches[0], string.Empty);
        var ordered = matches.Order().ToArray();
        var contiguous = ordered.Zip(ordered.Skip(1), (left, right) => right == left + 1).All(value => value);
        return contiguous
            ? (ordered[^1], $"monotonic state predicates also matched: {string.Join(",", ordered[..^1])}")
            : (-1, $"multiple non-contiguous workflow states match: {string.Join(",", ordered)}");
    }

    private static SemanticWorkflowRunResult Stop(
        string status,
        string state,
        string reason,
        SemanticWorkflowObservation observation,
        IReadOnlyList<SemanticWorkflowElement> candidates,
        List<SemanticWorkflowJournalEntry> journal,
        string stepId = "state-detection")
    {
        journal.Add(Entry(stepId, "abstain", state, observation, candidates, reason, "not-run", "not-run"));
        return new SemanticWorkflowRunResult(status, state, journal);
    }

    private static SemanticWorkflowJournalEntry Entry(
        string stepId,
        string phase,
        string state,
        SemanticWorkflowObservation observation,
        IReadOnlyList<SemanticWorkflowElement> candidates,
        string decision,
        string execution,
        string verification) => new(
            DateTimeOffset.UtcNow, stepId, phase, state,
            JsonSerializer.Serialize(new
            {
                observation.ProcessName,
                observation.WindowTitle,
                observation.Url,
                observation.Revision,
                elementCount = observation.Elements.Count,
            }),
            candidates.Select(candidate => candidate.Id).ToArray(),
            decision, execution, verification);

    private static SemanticWorkflowJournalEntry Failure(string stepId, Exception error) => new(
        DateTimeOffset.UtcNow, stepId, "observation", "unknown", error.Message,
        [], "observation failed", "not-run", "not-run");
}
