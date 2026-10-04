using SharpHook.Data;

namespace Series4.Desktop;

public static class RecordedSemanticWorkflowExtractor
{
    private static readonly TimeSpan MaximumTextKeyGap = TimeSpan.FromSeconds(2);

    public static SemanticWorkflowDefinition Compile(
        string naturalLanguageIntent,
        string videoPath,
        double videoDurationSeconds,
        IReadOnlyList<RecordedEvent> recordedEvents,
        SemanticDemonstrationFrame finalFrame)
    {
        ArgumentNullException.ThrowIfNull(recordedEvents);
        ArgumentNullException.ThrowIfNull(finalFrame);
        var meaningful = recordedEvents
            .Where(item => item.ActionKind is not MacroActionKind.None and not MacroActionKind.Wait)
            .OrderBy(item => item.Offset)
            .ThenBy(item => item.Sequence)
            .Select((item, index) => new IndexedEvent(index, item))
            .ToArray();
        if (meaningful.Length == 0)
            throw new InvalidOperationException("의미 Workflow로 만들 녹화 입력이 없습니다.");

        var units = ExtractUnits(meaningful, finalFrame);
        var stateFrames = BuildStateFrames(units, finalFrame);
        var actions = units.Select((unit, index) => new SemanticDemonstrationAction(
            $"recorded-{unit.Events[0].Event.Sequence}",
            unit.Events[0].Index,
            unit.Events[0].Event.Offset.TotalSeconds,
            unit.Kind,
            unit.Target,
            unit.Value,
            stateFrames[index].Id,
            stateFrames[index + 1].Id,
            unit.Events.Skip(1).Select(item => item.Index).ToArray(),
            unit.Events.SelectMany(item => new[] { item.Event.SemanticBefore?.Id, item.Event.SemanticAfter?.Id })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray())).ToArray();
        var inputEvents = meaningful.Select(item => new DemonstrationInputEvent(
            item.Index,
            item.Event.Offset.TotalSeconds,
            item.Event.ActionKind.ToString())).ToArray();

        var evidenceFrames = stateFrames
            .Concat(meaningful.SelectMany(item => new[] { item.Event.SemanticBefore, item.Event.SemanticAfter })
                .Where(frame => frame is not null)
                .Cast<SemanticDemonstrationFrame>())
            .GroupBy(frame => frame.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return SemanticWorkflowCompiler.Compile(new SemanticDemonstration(
            naturalLanguageIntent,
            videoPath,
            videoDurationSeconds,
            inputEvents,
            evidenceFrames,
            actions));
    }

    private static IReadOnlyList<ExtractedUnit> ExtractUnits(
        IReadOnlyList<IndexedEvent> events,
        SemanticDemonstrationFrame finalFrame)
    {
        var units = new List<ExtractedUnit>();
        for (var index = 0; index < events.Count;)
        {
            var current = events[index];
            if (current.Event.ActionKind == MacroActionKind.MouseLeftClick
                && current.Event.SemanticTarget is { } focusTarget
                && IsEditable(focusTarget)
                && index + 1 < events.Count
                && events[index + 1].Event.ActionKind == MacroActionKind.KeyStroke
                && SameTarget(focusTarget, events[index + 1].Event.SemanticTarget))
            {
                RequireClickEvidence(current.Event);
                var group = new List<IndexedEvent> { current };
                var cursor = index + 1;
                while (cursor < events.Count
                    && events[cursor].Event.ActionKind == MacroActionKind.KeyStroke
                    && SameTarget(focusTarget, events[cursor].Event.SemanticTarget)
                    && (cursor == index + 1
                        || events[cursor].Event.Offset - events[cursor - 1].Event.Offset <= MaximumTextKeyGap))
                {
                    RequireTextEvidence(events[cursor].Event);
                    group.Add(events[cursor]);
                    cursor++;
                }
                var after = group[^1].Event.SemanticAfter
                    ?? (cursor == events.Count ? finalFrame : null)
                    ?? throw new InvalidOperationException("편집 필드의 최종 값을 확인할 화면 증거가 없습니다.");
                units.Add(TextUnit(group, focusTarget, current.Event.SemanticBefore!, after));
                index = cursor;
                continue;
            }

            if (current.Event.ActionKind == MacroActionKind.MouseLeftClick)
            {
                RequireClickEvidence(current.Event);
                var kind = current.Event.SemanticTarget!.Roles.Any(role => role is "CheckBox" or "RadioButton")
                    ? "toggle"
                    : "click";
                units.Add(new ExtractedUnit(
                    [current], kind, current.Event.SemanticTarget!, null,
                    current.Event.SemanticBefore!, null));
                index++;
                continue;
            }

            if (current.Event.ActionKind == MacroActionKind.KeyStroke)
            {
                RequireTextEvidence(current.Event);
                var group = new List<IndexedEvent> { current };
                var cursor = index + 1;
                while (cursor < events.Count
                    && events[cursor].Event.ActionKind == MacroActionKind.KeyStroke
                    && SameTextTarget(current.Event, events[cursor].Event)
                    && events[cursor].Event.Offset - events[cursor - 1].Event.Offset <= MaximumTextKeyGap)
                {
                    RequireTextEvidence(events[cursor].Event);
                    group.Add(events[cursor]);
                    cursor++;
                }

                var before = group[0].Event.SemanticBefore!;
                var after = group[^1].Event.SemanticAfter
                    ?? (cursor == events.Count ? finalFrame : null)
                    ?? throw new InvalidOperationException("키 입력 뒤 편집 값을 확인할 화면 증거가 없습니다.");
                var target = group[0].Event.SemanticTarget!;
                units.Add(TextUnit(group, target, before, after));
                index = cursor;
                continue;
            }

            throw new InvalidOperationException(
                $"현재 자동 추출기가 지원하지 않는 녹화 이벤트입니다: {current.Event.Sequence}({current.Event.ActionKind})");
        }
        return units;
    }

    private static IReadOnlyList<SemanticDemonstrationFrame> BuildStateFrames(
        IReadOnlyList<ExtractedUnit> units,
        SemanticDemonstrationFrame finalFrame)
    {
        var states = new List<SemanticDemonstrationFrame>
        {
            MergeFrames("recorded-state-0", units[0].Before),
        };
        for (var index = 1; index < units.Count; index++)
            states.Add(MergeFrames($"recorded-state-{index}", units[index - 1].After, units[index].Before));
        states.Add(MergeFrames($"recorded-state-{units.Count}", units[^1].After, finalFrame));
        return states;
    }

    private static SemanticDemonstrationFrame MergeFrames(
        string id,
        params SemanticDemonstrationFrame?[] sources)
    {
        var available = sources.Where(item => item is not null).Cast<SemanticDemonstrationFrame>().ToArray();
        if (available.Length == 0) throw new InvalidOperationException("Workflow 상태 화면 근거가 없습니다.");
        var authoritative = available[^1];
        var sameWindow = available.Where(frame =>
            string.Equals(Normalize(frame.ProcessName), Normalize(authoritative.ProcessName), StringComparison.Ordinal)
            && string.Equals(frame.WindowTitle.Trim(), authoritative.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var elements = new Dictionary<string, SemanticElementEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in sameWindow)
        {
            foreach (var element in frame.Elements)
                elements[ElementIdentity(element)] = element;
        }
        return authoritative with
        {
            Id = id,
            OffsetSeconds = available.Max(item => item.OffsetSeconds),
            Elements = elements.Values.ToArray(),
        };
    }

    private static void RequireClickEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 클릭 의미 증거가 없습니다.");
    }

    private static void RequireTextEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 텍스트 의미 증거가 없습니다.");
        if (item.ModifierKeyCodes.Any(key => key is
            KeyCode.VcLeftControl or KeyCode.VcRightControl
            or KeyCode.VcLeftAlt or KeyCode.VcRightAlt
            or KeyCode.VcLeftMeta or KeyCode.VcRightMeta))
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 Ctrl/Alt/Win 조합은 텍스트 입력으로 자동 변환하지 않습니다.");
        if (item.SemanticTarget.Roles.Any(role => role is not ("Edit" or "Document" or "ComboBox")))
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 대상은 검증 가능한 편집 필드가 아닙니다.");
        if (SensitiveTarget(item.SemanticTarget.Name))
            throw new InvalidOperationException($"민감 입력 필드는 의미 Workflow로 만들 수 없습니다: {item.SemanticTarget.Name}");
    }

    private static bool SameTextTarget(RecordedEvent first, RecordedEvent second) =>
        first.SemanticTarget is not null
        && second.SemanticTarget is not null
        && string.Equals(TargetIdentity(first.SemanticTarget), TargetIdentity(second.SemanticTarget), StringComparison.OrdinalIgnoreCase)
        && first.SemanticBefore is not null
        && second.SemanticBefore is not null
        && string.Equals(Normalize(first.SemanticBefore.ProcessName), Normalize(second.SemanticBefore.ProcessName), StringComparison.Ordinal)
        && string.Equals(first.SemanticBefore.WindowTitle.Trim(), second.SemanticBefore.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ExtractedUnit TextUnit(
        IReadOnlyList<IndexedEvent> events,
        SemanticTargetSelector target,
        SemanticDemonstrationFrame before,
        SemanticDemonstrationFrame after)
    {
        var initialValue = TargetValue(before, target);
        var finalValue = TargetValue(after, target);
        if (finalValue is null || finalValue == initialValue)
            throw new InvalidOperationException(
                $"키 입력 {events[0].Event.Sequence}~{events[^1].Event.Sequence}의 편집 결과를 의미적으로 확인할 수 없습니다.");
        if (string.IsNullOrEmpty(finalValue))
            throw new InvalidOperationException("필드 전체 삭제는 현재 무인 텍스트 동작으로 만들 수 없습니다.");
        return new ExtractedUnit(events, "type", target, finalValue, before, after);
    }

    private static bool IsEditable(SemanticTargetSelector target) =>
        target.Roles.Any(role => role is "Edit" or "Document" or "ComboBox");

    private static bool SameTarget(SemanticTargetSelector expected, SemanticTargetSelector? actual) =>
        actual is not null
        && string.Equals(TargetIdentity(expected), TargetIdentity(actual), StringComparison.OrdinalIgnoreCase);

    private static string? TargetValue(SemanticDemonstrationFrame frame, SemanticTargetSelector target)
    {
        var matches = frame.Elements.Where(element =>
            target.Roles.Any(role => string.Equals(role, element.Role, StringComparison.OrdinalIgnoreCase))
            && string.Equals(target.Name.Trim(), element.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(target.AutomationId)
                || string.Equals(target.AutomationId, element.AutomationId, StringComparison.Ordinal))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    private static bool SensitiveTarget(string name) => new[]
    {
        "password", "passcode", "otp", "one-time", "비밀번호", "암호", "인증번호",
    }.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string TargetIdentity(SemanticTargetSelector target) =>
        $"{string.Join('|', target.Roles.Order(StringComparer.OrdinalIgnoreCase))}\u001f{target.Name}\u001f{target.AutomationId}";

    private static string ElementIdentity(SemanticElementEvidence element) =>
        $"{element.Role}\u001f{element.Name}\u001f{element.AutomationId}";

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private sealed record IndexedEvent(int Index, RecordedEvent Event);

    private sealed record ExtractedUnit(
        IReadOnlyList<IndexedEvent> Events,
        string Kind,
        SemanticTargetSelector Target,
        string? Value,
        SemanticDemonstrationFrame Before,
        SemanticDemonstrationFrame? After);
}
