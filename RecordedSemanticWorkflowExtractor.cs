using SharpHook.Data;

namespace Series4.Desktop;

public static class RecordedSemanticWorkflowExtractor
{
    private static readonly TimeSpan MaximumTextKeyGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumSelectionKeyGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumFocusKeyGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumWheelGap = TimeSpan.FromSeconds(1);

    public static SemanticWorkflowDefinition Compile(
        string naturalLanguageIntent,
        string videoPath,
        double videoDurationSeconds,
        IReadOnlyList<RecordedEvent> recordedEvents,
        SemanticDemonstrationFrame finalFrame,
        IReadOnlyDictionary<string, string>? videoFrameHashes = null)
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
            .Select(frame => videoFrameHashes is not null
                ? frame with
                {
                    VideoFrameSha256 = ResolveVideoFrameHash(frame, videoFrameHashes, meaningful, finalFrame),
                }
                : frame)
            .ToArray();
        return SemanticWorkflowCompiler.Compile(new SemanticDemonstration(
            naturalLanguageIntent,
            videoPath,
            videoDurationSeconds,
            inputEvents,
            evidenceFrames,
            actions));
    }

    private static string? ResolveVideoFrameHash(
        SemanticDemonstrationFrame frame,
        IReadOnlyDictionary<string, string> hashes,
        IReadOnlyList<IndexedEvent> events,
        SemanticDemonstrationFrame finalFrame)
    {
        if (hashes.TryGetValue(frame.Id, out var direct)) return direct;
        return events
            .SelectMany(item => new[] { item.Event.SemanticBefore, item.Event.SemanticAfter })
            .Append(finalFrame)
            .Where(candidate => candidate is not null
                && Math.Abs(candidate.OffsetSeconds - frame.OffsetSeconds) < .000001)
            .Select(candidate => hashes.TryGetValue(candidate!.Id, out var hash) ? hash : null)
            .FirstOrDefault(hash => hash is not null);
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

            if (current.Event.ActionKind == MacroActionKind.KeyStroke
                && TryGetActivationKind(current.Event, out var activationKind))
            {
                RequireActivationEvidence(current.Event);
                var after = current.Event.SemanticAfter
                    ?? (index + 1 == events.Count ? finalFrame : null)
                    ?? throw new InvalidOperationException("키보드 활성화 뒤 의미 화면을 확인할 수 없습니다.");
                units.Add(new ExtractedUnit(
                    [current],
                    activationKind,
                    current.Event.SemanticTarget!,
                    null,
                    current.Event.SemanticBefore!,
                    after));
                index++;
                continue;
            }

            if (current.Event.ActionKind == MacroActionKind.KeyStroke
                && IsSelectionNavigation(current.Event))
            {
                RequireSelectionEvidence(current.Event);
                var group = new List<IndexedEvent> { current };
                var cursor = index + 1;
                while (cursor < events.Count
                    && events[cursor].Event.ActionKind == MacroActionKind.KeyStroke
                    && IsSelectionNavigation(events[cursor].Event)
                    && SameTarget(current.Event.SemanticTarget!, events[cursor].Event.SemanticTarget)
                    && events[cursor].Event.Offset - events[cursor - 1].Event.Offset <= MaximumSelectionKeyGap)
                {
                    RequireSelectionEvidence(events[cursor].Event);
                    group.Add(events[cursor]);
                    cursor++;
                }
                var before = group[0].Event.SemanticBefore!;
                var after = group[^1].Event.SemanticAfter
                    ?? throw new InvalidOperationException("선택 탐색 뒤 의미 화면을 확인할 수 없습니다.");
                var initialValue = TargetValue(before, current.Event.SemanticTarget!);
                var finalValue = TargetValue(after, current.Event.SemanticTarget!);
                if (string.IsNullOrWhiteSpace(finalValue) || finalValue == initialValue || finalValue.Length > 1000)
                    throw new InvalidOperationException($"이벤트 {current.Event.Sequence}의 최종 선택값을 의미적으로 확인할 수 없습니다.");
                units.Add(new ExtractedUnit(
                    group,
                    "select-option",
                    current.Event.SemanticTarget!,
                    finalValue,
                    before,
                    after));
                index = cursor;
                continue;
            }

            if (current.Event.ActionKind == MacroActionKind.KeyStroke
                && IsFocusNavigation(current.Event))
            {
                RequireFocusEvidence(current.Event);
                var group = new List<IndexedEvent> { current };
                var cursor = index + 1;
                while (cursor < events.Count
                    && events[cursor].Event.ActionKind == MacroActionKind.KeyStroke
                    && IsFocusNavigation(events[cursor].Event)
                    && SameWindow(current.Event.SemanticBefore!, events[cursor].Event.SemanticBefore!)
                    && events[cursor].Event.Offset - events[cursor - 1].Event.Offset <= MaximumFocusKeyGap)
                {
                    RequireFocusEvidence(events[cursor].Event);
                    group.Add(events[cursor]);
                    cursor++;
                }
                var before = group[0].Event.SemanticBefore!;
                var after = group[^1].Event.SemanticAfter!;
                var initialTarget = FocusedTarget(before, current.Event.Sequence);
                var finalTarget = FocusedTarget(after, group[^1].Event.Sequence);
                if (SameTarget(initialTarget, finalTarget))
                    throw new InvalidOperationException($"이벤트 {current.Event.Sequence}의 Tab 탐색 결과 포커스가 바뀌지 않았습니다.");
                units.Add(new ExtractedUnit(
                    group,
                    "focus",
                    finalTarget,
                    null,
                    before,
                    after));
                index = cursor;
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

            if (current.Event.ActionKind == MacroActionKind.MouseDrag)
            {
                RequireRangeDragEvidence(current.Event);
                var after = current.Event.SemanticAfter
                    ?? throw new InvalidOperationException("드래그 뒤 의미 범위 값을 확인할 수 없습니다.");
                var beforeValue = TargetValue(current.Event.SemanticBefore!, current.Event.SemanticTarget!);
                var afterValue = TargetValue(after, current.Event.SemanticTarget!);
                if (!SemanticRangeValue.TryParse(beforeValue, out var initial)
                    || !SemanticRangeValue.TryParse(afterValue, out var final)
                    || initial == final)
                    throw new InvalidOperationException($"이벤트 {current.Event.Sequence}의 드래그 결과 값을 의미적으로 확인할 수 없습니다.");
                units.Add(new ExtractedUnit(
                    [current],
                    "set-range",
                    current.Event.SemanticTarget!,
                    SemanticRangeValue.Format(final),
                    current.Event.SemanticBefore!,
                    after));
                index++;
                continue;
            }

            if (current.Event.ActionKind == MacroActionKind.MouseWheel)
            {
                RequireScrollEvidence(current.Event);
                var group = new List<IndexedEvent> { current };
                var cursor = index + 1;
                while (cursor < events.Count
                    && events[cursor].Event.ActionKind == MacroActionKind.MouseWheel
                    && SameTarget(current.Event.SemanticTarget!, events[cursor].Event.SemanticTarget)
                    && current.Event.IsHorizontalWheel == events[cursor].Event.IsHorizontalWheel
                    && Math.Sign(current.Event.WheelRotation) == Math.Sign(events[cursor].Event.WheelRotation)
                    && events[cursor].Event.Offset - events[cursor - 1].Event.Offset <= MaximumWheelGap)
                {
                    RequireScrollEvidence(events[cursor].Event);
                    group.Add(events[cursor]);
                    cursor++;
                }
                var after = group[^1].Event.SemanticAfter
                    ?? throw new InvalidOperationException("휠 입력 뒤 의미 화면을 확인할 수 없습니다.");
                var notches = Math.Clamp(group.Count, 1, 20);
                var axis = current.Event.IsHorizontalWheel ? "horizontal" : "vertical";
                var direction = current.Event.WheelRotation < 0 ? "increment" : "decrement";
                units.Add(new ExtractedUnit(
                    group,
                    "scroll",
                    current.Event.SemanticTarget!,
                    new SemanticScrollCommand(axis, direction, notches).ToString(),
                    current.Event.SemanticBefore!,
                    after));
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

    private static bool TryGetActivationKind(RecordedEvent item, out string kind)
    {
        kind = string.Empty;
        if (item.SemanticTarget is null || item.ModifierKeyCodes.Length > 0) return false;
        var keys = item.KeyCodes.Where(key => key is not (
            KeyCode.VcLeftShift or KeyCode.VcRightShift
            or KeyCode.VcLeftControl or KeyCode.VcRightControl
            or KeyCode.VcLeftAlt or KeyCode.VcRightAlt
            or KeyCode.VcLeftMeta or KeyCode.VcRightMeta)).ToArray();
        if (keys.Length != 1) return false;
        var roles = item.SemanticTarget.Roles;
        if (keys[0] == KeyCode.VcSpace && roles.Any(role => role is "CheckBox" or "RadioButton"))
        {
            kind = "toggle";
            return true;
        }
        if (keys[0] is KeyCode.VcEnter or KeyCode.VcSpace
            && roles.Any(role => role is "Button" or "MenuItem" or "Hyperlink"))
        {
            kind = "click";
            return true;
        }
        return false;
    }

    private static void RequireActivationEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 키보드 활성화 의미 증거가 없습니다.");
        if (item.SemanticAfter is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 키보드 활성화 이후 화면 증거가 없습니다.");
    }

    private static bool IsSelectionNavigation(RecordedEvent item)
    {
        if (item.SemanticTarget is null
            || item.ModifierKeyCodes.Length > 0
            || !item.SemanticTarget.Roles.Any(role => role is "ComboBox" or "List"))
            return false;
        var keys = item.KeyCodes.Where(key => key is not (
            KeyCode.VcLeftShift or KeyCode.VcRightShift
            or KeyCode.VcLeftControl or KeyCode.VcRightControl
            or KeyCode.VcLeftAlt or KeyCode.VcRightAlt
            or KeyCode.VcLeftMeta or KeyCode.VcRightMeta)).ToArray();
        return keys.Length == 1 && keys[0] is
            KeyCode.VcUp or KeyCode.VcDown or KeyCode.VcHome or KeyCode.VcEnd
            or KeyCode.VcPageUp or KeyCode.VcPageDown;
    }

    private static void RequireSelectionEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticAfter is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 선택 전후 의미 증거가 없습니다.");
    }

    private static bool IsFocusNavigation(RecordedEvent item)
    {
        if (item.SemanticTarget is null
            || item.ModifierKeyCodes.Any(key => key is not (KeyCode.VcLeftShift or KeyCode.VcRightShift)))
            return false;
        var keys = item.KeyCodes.Where(key => key is not (KeyCode.VcLeftShift or KeyCode.VcRightShift)).ToArray();
        return keys.Length == 1 && keys[0] == KeyCode.VcTab;
    }

    private static void RequireFocusEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticAfter is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 Tab 포커스 탐색 전후 의미 증거가 없습니다.");
        _ = FocusedTarget(item.SemanticBefore, item.Sequence);
        _ = FocusedTarget(item.SemanticAfter, item.Sequence);
    }

    private static SemanticTargetSelector FocusedTarget(SemanticDemonstrationFrame frame, long sequence)
    {
        var focused = frame.Elements.Where(element => element.KeyboardFocused
            && element.Enabled
            && !element.Password
            && !element.Offscreen
            && !string.IsNullOrWhiteSpace(element.Name)).Take(2).ToArray();
        if (focused.Length != 1)
            throw new InvalidOperationException($"이벤트 {sequence}의 화면에서 키보드 포커스 대상을 정확히 하나 확인할 수 없습니다: {focused.Length}개");
        var element = focused[0];
        return new SemanticTargetSelector(
            [element.Role],
            element.Name,
            string.IsNullOrWhiteSpace(element.AutomationId) ? null : element.AutomationId,
            element.Offscreen);
    }

    private static bool SameWindow(SemanticDemonstrationFrame first, SemanticDemonstrationFrame second) =>
        string.Equals(Normalize(first.ProcessName), Normalize(second.ProcessName), StringComparison.Ordinal)
        && string.Equals(first.WindowTitle.Trim(), second.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void RequireScrollEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticAfter is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 스크롤 전후 의미 증거가 없습니다.");
        if (item.WheelRotation == 0)
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 스크롤 방향을 확인할 수 없습니다.");
        if (item.ModifierKeyCodes.Length > 0)
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 수정키 결합 스크롤은 자동 변환하지 않습니다.");
    }

    private static void RequireRangeDragEvidence(RecordedEvent item)
    {
        if (item.SemanticBefore is null || item.SemanticAfter is null || item.SemanticTarget is null)
            throw new InvalidOperationException($"이벤트 {item.Sequence}에 드래그 전후 의미 증거가 없습니다.");
        if (item.DragButton != MouseButton.Button1)
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 범위 조절은 왼쪽 버튼 드래그만 지원합니다.");
        if (item.ModifierKeyCodes.Length > 0)
            throw new InvalidOperationException($"이벤트 {item.Sequence}의 수정키 결합 드래그는 자동 변환하지 않습니다.");
        if (!item.SemanticTarget.Roles.Any(role => role == "Slider"))
            throw new InvalidOperationException($"이벤트 {item.Sequence}는 의미 값을 제공하는 슬라이더 드래그가 아닙니다.");
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
