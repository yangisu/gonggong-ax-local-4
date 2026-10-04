namespace Series4.Desktop;

public static class RecordedSemanticWorkflowExtractor
{
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
            .ToArray();
        if (meaningful.Length == 0)
            throw new InvalidOperationException("의미 Workflow로 만들 녹화 입력이 없습니다.");

        var unsupported = meaningful.Where(item =>
            item.ActionKind != MacroActionKind.MouseLeftClick
            || item.SemanticBefore is null
            || item.SemanticTarget is null).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidOperationException(
                $"현재 자동 추출기는 의미 대상이 기록된 왼쪽 클릭만 지원합니다. 재검토할 이벤트: {string.Join(",", unsupported.Select(item => item.Sequence))}");

        var inputEvents = meaningful.Select((item, index) => new DemonstrationInputEvent(
            index,
            item.Offset.TotalSeconds,
            item.ActionKind.ToString())).ToArray();
        var frames = meaningful.Select(item => item.SemanticBefore!).Append(finalFrame).ToArray();
        var actions = new List<SemanticDemonstrationAction>();
        for (var index = 0; index < meaningful.Length; index++)
        {
            var item = meaningful[index];
            var target = item.SemanticTarget!;
            var kind = target.Roles.Any(role => role is "CheckBox" or "RadioButton") ? "toggle" : "click";
            actions.Add(new SemanticDemonstrationAction(
                $"recorded-{item.Sequence}",
                index,
                item.Offset.TotalSeconds,
                kind,
                target,
                null,
                item.SemanticBefore!.Id,
                index + 1 < meaningful.Length ? meaningful[index + 1].SemanticBefore!.Id : finalFrame.Id));
        }

        var demonstration = new SemanticDemonstration(
            naturalLanguageIntent,
            videoPath,
            videoDurationSeconds,
            inputEvents,
            frames,
            actions);
        return SemanticWorkflowCompiler.Compile(demonstration);
    }
}
