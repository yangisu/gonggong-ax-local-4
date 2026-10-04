using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace Series4.Desktop;

public sealed class WindowsSemanticWorkflowSurface : ISemanticWorkflowSurface
{
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public SemanticWorkflowObservation Observe()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { return ObserveOnce(); }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                last = error;
                Thread.Sleep(150);
            }
        }
        throw new InvalidOperationException("Windows 의미 화면을 안정적으로 관찰하지 못했습니다.", last);
    }

    public static (SemanticDemonstrationFrame Frame, SemanticTargetSelector? Target) CaptureDemonstrationFrame(
        string id,
        double offsetSeconds,
        double? screenX = null,
        double? screenY = null)
    {
        var observation = ObserveOnce();
        SemanticTargetSelector? target = null;
        if (screenX is double x && screenY is double y && double.IsFinite(x) && double.IsFinite(y))
        {
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (element is not null
                    && element.Current.ProcessId != Environment.ProcessId
                    && element.Current.ProcessId != (int)MainWindow.BridgeParentPid
                    && !element.Current.IsPassword
                    && !string.IsNullOrWhiteSpace(element.Current.Name))
                    target = new SemanticTargetSelector(
                        [Role(element)],
                        element.Current.Name,
                        string.IsNullOrWhiteSpace(element.Current.AutomationId) ? null : element.Current.AutomationId,
                        element.Current.IsOffscreen);
            }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                target = null;
            }
        }
        var frame = new SemanticDemonstrationFrame(
            id,
            offsetSeconds,
            observation.ProcessName,
            observation.WindowTitle,
            observation.Url,
            observation.Elements.Select(element => new SemanticElementEvidence(
                element.Role,
                element.Name,
                element.AutomationId,
                element.Value,
                element.Enabled,
                element.Offscreen,
                element.Password,
                element.KeyboardFocused,
                element.Bounds)).ToArray());
        return (frame, target);
    }

    public static (SemanticDemonstrationFrame Frame, SemanticTargetSelector? Target) CapturePointDemonstrationFrame(
        string id,
        double offsetSeconds,
        double screenX,
        double screenY)
    {
        var observation = ObserveOnce();
        var root = ForegroundRoot();
        var processId = root.Current.ProcessId;
        var element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        AutomationElement? fallback = null;
        SemanticTargetSelector? target = null;
        while (element is not null && element.Current.ProcessId == processId)
        {
            if (!element.Current.IsPassword && !string.IsNullOrWhiteSpace(element.Current.Name))
            {
                fallback ??= element;
                if (IsActionablePointerTarget(element))
                {
                    fallback = element;
                    break;
                }
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        if (fallback is not null)
        {
            var automationId = fallback.Current.AutomationId ?? string.Empty;
            var role = Role(fallback);
            SemanticVisualAnchor? visualAnchor = null;
            if (role == "Window")
            {
                var handle = new IntPtr(fallback.Current.NativeWindowHandle);
                visualAnchor = WindowsVisualAnchor.CaptureUniqueAtScreenPoint(
                    handle,
                    checked((int)Math.Round(screenX)),
                    checked((int)Math.Round(screenY)));
            }
            target = new SemanticTargetSelector(
                [role],
                fallback.Current.Name,
                string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                fallback.Current.IsOffscreen,
                VisualAnchor: visualAnchor);
        }
        return (ToFrame(id, offsetSeconds, observation), target);
    }

    public static (SemanticDemonstrationFrame Frame, SemanticTargetSelector? Target) CaptureFocusedDemonstrationFrame(
        string id,
        double offsetSeconds,
        bool includeAllElements = false)
    {
        var observation = includeAllElements ? ObserveOnce() : null;
        var root = ForegroundRoot();
        var processId = root.Current.ProcessId;
        if (processId == Environment.ProcessId || processId == (int)MainWindow.BridgeParentPid)
            throw new InvalidOperationException("자동화 호스트 자체는 시연 대상으로 사용할 수 없습니다.");
        string process;
        try { process = Process.GetProcessById(processId).ProcessName; }
        catch { process = string.Empty; }
        var element = AutomationElement.FocusedElement;
        SemanticTargetSelector? target = null;
        SemanticElementEvidence[] elements = [];
        if (element is not null
            && element.Current.ProcessId == processId
            && element.Current.IsEnabled
            && !element.Current.IsPassword
            && !string.IsNullOrWhiteSpace(element.Current.Name))
        {
            var automationId = element.Current.AutomationId ?? string.Empty;
            var role = Role(element);
            var value = ReadValue(element);
            target = new SemanticTargetSelector(
                [role], element.Current.Name,
                string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                element.Current.IsOffscreen);
            elements =
            [
                new SemanticElementEvidence(
                    role, element.Current.Name, automationId, value,
                    element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.IsPassword, true),
            ];
        }
        if (observation is not null)
        {
            if (!SameProcess(observation.ProcessName, process)
                || !string.Equals(observation.WindowTitle, root.Current.Name ?? string.Empty, StringComparison.Ordinal))
                throw new InvalidOperationException("Tab 입력의 의미 화면을 캡처하는 동안 전경 창이 바뀌었습니다.");
            process = observation.ProcessName;
            elements = observation.Elements.Select(item => new SemanticElementEvidence(
                item.Role, item.Name, item.AutomationId, item.Value,
                item.Enabled, item.Offscreen, item.Password, item.KeyboardFocused)).ToArray();
            if (target is not null && !elements.Any(item => item.KeyboardFocused))
            {
                elements = elements.Append(new SemanticElementEvidence(
                    target.Roles[0], target.Name, target.AutomationId ?? string.Empty,
                    ReadValue(element!), element!.Current.IsEnabled, element.Current.IsOffscreen,
                    element.Current.IsPassword, true)).ToArray();
            }
        }
        return (
            new SemanticDemonstrationFrame(
                id, offsetSeconds, process, observation?.WindowTitle ?? root.Current.Name ?? string.Empty,
                observation?.Url ?? string.Empty, elements),
            target);
    }

    public static (SemanticDemonstrationFrame Frame, SemanticTargetSelector? Target) CaptureScrollableDemonstrationFrame(
        string id,
        double offsetSeconds,
        double screenX,
        double screenY)
    {
        var observation = ObserveOnce();
        var root = ForegroundRoot();
        var processId = root.Current.ProcessId;
        var element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        while (element is not null && element.Current.ProcessId == processId)
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out _)
                && !element.Current.IsPassword
                && element.Current.IsEnabled
                && !string.IsNullOrWhiteSpace(element.Current.Name))
            {
                var automationId = element.Current.AutomationId ?? string.Empty;
                var target = new SemanticTargetSelector(
                    [Role(element)],
                    element.Current.Name,
                    string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                    element.Current.IsOffscreen);
                return (ToFrame(id, offsetSeconds, observation), target);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return (ToFrame(id, offsetSeconds, observation), null);
    }

    public void Execute(SemanticPlannedAction action, SemanticWorkflowObservation observation)
    {
        var current = Observe();
        if (current.Revision != observation.Revision
            || !SameProcess(current.ProcessName, observation.ProcessName)
            || !string.Equals(current.WindowTitle, observation.WindowTitle, StringComparison.Ordinal))
            throw new InvalidOperationException("STALE_ACTION: 실행 전에 전경 화면이 달라졌습니다.");
        var matches = SemanticWorkflowMatching.Find(current, action.Target);
        if (matches.Count != 1 || matches[0].Id != action.TargetId)
            throw new InvalidOperationException("TARGET_MISMATCH: 실행 대상이 더 이상 정확히 하나가 아닙니다.");

        var root = ForegroundRoot();
        var element = FindExact(root, action.Target, includeOffscreen: action.Target.AllowOffscreen);
        if (element.Current.IsPassword)
            throw new InvalidOperationException("BLOCKED: 비밀번호 필드는 자동화할 수 없습니다.");
        if (element.Current.IsOffscreen)
        {
            if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
                throw new InvalidOperationException("TARGET_MISMATCH: 화면 밖 대상을 안전하게 표시할 수 없습니다.");
            ((ScrollItemPattern)scroll).ScrollIntoView();
            Thread.Sleep(150);
            root = ForegroundRoot();
            element = FindExact(root, action.Target, includeOffscreen: false);
        }

        switch (action.Kind)
        {
            case "type":
            case "select":
                if (action.Value is not { Length: > 0 } value || value.Length > 10000)
                    throw new InvalidOperationException("입력 값은 1~10000자여야 합니다.");
                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                    throw new InvalidOperationException("TARGET_MISMATCH: 대상이 의미 기반 텍스트 입력을 지원하지 않습니다.");
                ((ValuePattern)valuePattern).SetValue(value);
                break;
            case "select-option":
                if (action.Value is not { Length: > 0 } option || option.Length > 1000)
                    throw new InvalidOperationException("선택 항목은 1~1000자여야 합니다.");
                SelectUniqueOption(element, option);
                break;
            case "toggle":
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
                    ((TogglePattern)togglePattern).Toggle();
                else
                    InvokeOrClick(element);
                break;
            case "click":
                InvokeOrClick(element);
                break;
            case "visual-click":
                if (action.Target.VisualAnchor is not { } clickAnchor)
                    throw new InvalidOperationException("시각 클릭 대상 근거가 없습니다.");
                VisualClick(element, clickAnchor);
                break;
            case "scroll":
                if (!SemanticScrollCommand.TryParse(action.Value, out var command))
                    throw new InvalidOperationException("스크롤 동작 값이 올바르지 않습니다.");
                if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
                    throw new InvalidOperationException("TARGET_MISMATCH: 대상이 의미 기반 스크롤을 지원하지 않습니다.");
                var scroll = (ScrollPattern)scrollPattern;
                var increment = command.Direction == "increment"
                    ? ScrollAmount.SmallIncrement
                    : ScrollAmount.SmallDecrement;
                for (var index = 0; index < command.Count; index++)
                    scroll.Scroll(
                        command.Axis == "horizontal" ? increment : ScrollAmount.NoAmount,
                        command.Axis == "vertical" ? increment : ScrollAmount.NoAmount);
                break;
            case "set-range":
                if (!SemanticRangeValue.TryParse(action.Value, out var rangeValue))
                    throw new InvalidOperationException("범위 조절 값이 올바르지 않습니다.");
                if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangePattern))
                    throw new InvalidOperationException("TARGET_MISMATCH: 대상이 의미 기반 범위 조절을 지원하지 않습니다.");
                var range = (RangeValuePattern)rangePattern;
                if (range.Current.IsReadOnly
                    || rangeValue < range.Current.Minimum
                    || rangeValue > range.Current.Maximum)
                    throw new InvalidOperationException("TARGET_MISMATCH: 범위 조절 값이 현재 대상의 허용 범위를 벗어났습니다.");
                range.SetValue(rangeValue);
                break;
            case "focus":
                element.SetFocus();
                break;
            case "reorder-item":
                if (!SemanticOrdinalValue.TryParse(action.Value, out var ordinal))
                    throw new InvalidOperationException("목록 순서 값이 올바르지 않습니다.");
                ReorderListItem(element, ordinal);
                break;
            case "drag-within":
                if (!SemanticRelativeDrag.TryParse(action.Value, out var drag))
                    throw new InvalidOperationException("작업 영역 드래그 값이 올바르지 않습니다.");
                DragWithin(element, drag);
                break;
            case "visual-drag":
                if (!SemanticRelativePoint.TryParse(action.Value, out var endPoint)
                    || action.Target.VisualAnchor is not { } visualAnchor)
                    throw new InvalidOperationException("시각 대상 드래그 값이 올바르지 않습니다.");
                VisualDrag(element, visualAnchor, endPoint);
                break;
            default:
                throw new InvalidOperationException($"지원하지 않는 의미 동작입니다: {action.Kind}");
        }
    }

    private static SemanticWorkflowObservation ObserveOnce()
    {
        var root = ForegroundRoot();
        var processId = root.Current.ProcessId;
        if (processId == Environment.ProcessId || processId == (int)MainWindow.BridgeParentPid)
            throw new InvalidOperationException("자동화 호스트 자체는 대상으로 사용할 수 없습니다.");
        string process;
        try { process = Process.GetProcessById(processId).ProcessName; }
        catch { process = string.Empty; }
        var title = root.Current.Name ?? string.Empty;
        string? focusedRuntimeId = null;
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null && focused.Current.ProcessId == processId)
                focusedRuntimeId = RuntimeIdentity(focused);
        }
        catch (ElementNotAvailableException) { }
        var elements = EnumerateWithRoot(root, 600).Select(element => ToElement(element, focusedRuntimeId)).ToArray();
        var url = ReadActiveUrl(root);
        return new SemanticWorkflowObservation(process, title, url, elements, Revision(process, title, url, elements));
    }

    private static SemanticDemonstrationFrame ToFrame(
        string id,
        double offsetSeconds,
        SemanticWorkflowObservation observation) => new(
            id,
            offsetSeconds,
            observation.ProcessName,
            observation.WindowTitle,
            observation.Url,
            observation.Elements.Select(element => new SemanticElementEvidence(
                element.Role,
                element.Name,
                element.AutomationId,
                element.Value,
                element.Enabled,
                element.Offscreen,
                element.Password,
                element.KeyboardFocused,
                element.Bounds)).ToArray());

    private static AutomationElement ForegroundRoot()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("전경 Windows 창이 없습니다.");
        return AutomationElement.FromHandle(window)
            ?? throw new InvalidOperationException("전경 창이 UI Automation 정보를 제공하지 않습니다.");
    }

    private static AutomationElement FindExact(
        AutomationElement root,
        SemanticTargetSelector selector,
        bool includeOffscreen)
    {
        var candidates = EnumerateWithRoot(root, 800).Where(element =>
        {
            try
            {
                return element.Current.IsEnabled
                    && !element.Current.IsPassword
                    && (includeOffscreen || !element.Current.IsOffscreen)
                    && selector.Roles.Any(role => string.Equals(role, Role(element), StringComparison.OrdinalIgnoreCase))
                    && string.Equals(selector.Name.Trim(), (element.Current.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(selector.AutomationId)
                        || string.Equals(selector.AutomationId, element.Current.AutomationId, StringComparison.Ordinal))
                    && (selector.ExpectedValue is null
                        || string.Equals(selector.ExpectedValue, ReadValue(element), StringComparison.Ordinal))
                    && (!selector.RequireKeyboardFocus || element.Current.HasKeyboardFocus);
            }
            catch (ElementNotAvailableException) { return false; }
        }).Take(2).ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw new InvalidOperationException($"TARGET_MISMATCH: 의미 대상이 {candidates.Length}개입니다.");
    }

    private static IEnumerable<AutomationElement> Enumerate(AutomationElement root, int maximum)
    {
        var collection = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var accepted = 0;
        for (var index = 0; index < collection.Count && accepted < maximum; index++)
        {
            AutomationElement element;
            try
            {
                element = collection[index];
                _ = element.Current.ControlType;
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1) continue;
            }
            catch (ElementNotAvailableException) { continue; }
            accepted++;
            yield return element;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateWithRoot(AutomationElement root, int maximum)
    {
        if (maximum < 1) yield break;
        var bounds = root.Current.BoundingRectangle;
        if (!bounds.IsEmpty && bounds.Width > 1 && bounds.Height > 1)
            yield return root;
        foreach (var element in Enumerate(root, maximum - 1))
            yield return element;
    }

    private static SemanticWorkflowElement ToElement(AutomationElement element, string? focusedRuntimeId = null)
    {
        var role = Role(element);
        var name = element.Current.Name ?? string.Empty;
        var automationId = element.Current.AutomationId ?? string.Empty;
        var runtime = RuntimeIdentity(element);
        var idSource = $"{runtime}|{role}|{name}|{automationId}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource))).ToLowerInvariant()[..24];
        var rectangle = element.Current.BoundingRectangle;
        return new SemanticWorkflowElement(
            id, role, name, automationId, ReadValue(element),
            element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.IsPassword,
            focusedRuntimeId is not null && string.Equals(runtime, focusedRuntimeId, StringComparison.Ordinal),
            new SemanticBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
    }

    private static string RuntimeIdentity(AutomationElement element) =>
        string.Join('.', element.GetRuntimeId());

    private static string Role(AutomationElement element) =>
        element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);

    private static string ReadValue(AutomationElement element)
    {
        if (element.Current.IsPassword) return string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var value = ((ValuePattern)valuePattern).Current.Value ?? string.Empty;
                return value[..Math.Min(2000, value.Length)];
            }
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
                return ((TogglePattern)togglePattern).Current.ToggleState.ToString();
            if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangePattern))
                return ((RangeValuePattern)rangePattern).Current.Value.ToString("R", CultureInfo.InvariantCulture);
            if (TryGetListItemOrdinal(element, out var ordinal))
                return SemanticOrdinalValue.FormatEvidence(ordinal);
            if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selectionPattern))
            {
                var selection = ((SelectionPattern)selectionPattern).Current.GetSelection()
                    .Select(item => item.Current.Name ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                if (selection.Length == 1) return selection[0];
            }
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
            {
                var current = ((ScrollPattern)scrollPattern).Current;
                return string.Create(CultureInfo.InvariantCulture,
                    $"horizontal={current.HorizontalScrollPercent:0.###};vertical={current.VerticalScrollPercent:0.###}");
            }
            return string.Empty;
        }
        catch (ElementNotAvailableException) { return string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private static string ReadActiveUrl(AutomationElement root)
    {
        try
        {
            var documents = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            var active = new List<string>();
            for (var index = 0; index < documents.Count; index++)
            {
                var document = documents[index];
                if (document.Current.IsOffscreen || !document.Current.HasKeyboardFocus) continue;
                var value = ReadValue(document);
                if (Uri.IsWellFormedUriString(value, UriKind.Absolute)) active.Add(value);
            }
            if (active.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1) return active[0];

            var address = Enumerate(root, 600).Where(element =>
                string.Equals(element.Current.AutomationId, "view_1012", StringComparison.Ordinal)
                || (element.Current.Name ?? string.Empty).Contains("주소", StringComparison.OrdinalIgnoreCase)
                || (element.Current.Name ?? string.Empty).Contains("address", StringComparison.OrdinalIgnoreCase))
                .Select(ReadValue)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(address)) return string.Empty;
            return Uri.IsWellFormedUriString(address, UriKind.Absolute) ? address : $"https://{address}";
        }
        catch (ElementNotAvailableException) { return string.Empty; }
    }

    private static long Revision(
        string process,
        string title,
        string url,
        IReadOnlyList<SemanticWorkflowElement> elements)
    {
        var source = new StringBuilder().Append(process).Append('\u001f').Append(title).Append('\u001f').Append(url);
        foreach (var element in elements.OrderBy(item => item.Id, StringComparer.Ordinal))
            source.Append('\u001e').Append(element.Id).Append('|').Append(element.Value).Append('|')
                .Append(element.Offscreen).Append('|').Append(element.KeyboardFocused);
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())), 0);
    }

    private static void InvokeOrClick(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return;
        }
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
            throw new InvalidOperationException("TARGET_MISMATCH: 대상에 클릭 가능한 영역이 없습니다.");
        var x = checked((int)Math.Round(bounds.Left + bounds.Width / 2));
        var y = checked((int)Math.Round(bounds.Top + bounds.Height / 2));
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("마우스 포인터를 의미 대상에 이동하지 못했습니다.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    private static void ReorderListItem(AutomationElement element, int finalOrdinal)
    {
        var siblings = ListItemsFor(element);
        if (siblings.Length < 2 || finalOrdinal >= siblings.Length)
            throw new InvalidOperationException("TARGET_MISMATCH: 요청한 목록 위치가 현재 목록 범위를 벗어났습니다.");
        var runtime = RuntimeIdentity(element);
        var currentOrdinal = Array.FindIndex(siblings,
            item => string.Equals(RuntimeIdentity(item), runtime, StringComparison.Ordinal));
        if (currentOrdinal < 0)
            throw new InvalidOperationException("TARGET_MISMATCH: 이동할 항목이 현재 목록에 없습니다.");
        if (currentOrdinal == finalOrdinal)
            throw new InvalidOperationException("TARGET_MISMATCH: 목록 항목이 이미 요청한 위치에 있습니다.");
        var destination = siblings[finalOrdinal];
        if (destination.Current.IsOffscreen)
        {
            if (!destination.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
                throw new InvalidOperationException("TARGET_MISMATCH: 목표 목록 위치를 화면에 표시할 수 없습니다.");
            ((ScrollItemPattern)scroll).ScrollIntoView();
            Thread.Sleep(150);
        }
        var sourceBounds = element.Current.BoundingRectangle;
        var destinationBounds = destination.Current.BoundingRectangle;
        if (sourceBounds.IsEmpty || destinationBounds.IsEmpty
            || sourceBounds.Width <= 1 || sourceBounds.Height <= 1
            || destinationBounds.Width <= 1 || destinationBounds.Height <= 1)
            throw new InvalidOperationException("TARGET_MISMATCH: 목록 재정렬 드래그 영역을 확인할 수 없습니다.");
        var startX = checked((int)Math.Round(sourceBounds.Left + sourceBounds.Width / 2));
        var startY = checked((int)Math.Round(sourceBounds.Top + sourceBounds.Height / 2));
        var endX = checked((int)Math.Round(destinationBounds.Left + destinationBounds.Width / 2));
        var endY = checked((int)Math.Round(destinationBounds.Top + destinationBounds.Height / 2));
        if (!SetCursorPos(startX, startY))
            throw new InvalidOperationException("목록 항목으로 마우스 포인터를 이동하지 못했습니다.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            for (var step = 1; step <= 10; step++)
            {
                var x = startX + (endX - startX) * step / 10;
                var y = startY + (endY - startY) * step / 10;
                if (!SetCursorPos(x, y))
                    throw new InvalidOperationException("목록 재정렬 경로로 마우스 포인터를 이동하지 못했습니다.");
                Thread.Sleep(25);
            }
        }
        finally
        {
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static void DragWithin(AutomationElement element, SemanticRelativeDrag drag)
    {
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
            throw new InvalidOperationException("TARGET_MISMATCH: 작업 영역 경계를 확인할 수 없습니다.");
        var startX = checked((int)Math.Round(bounds.Left + bounds.Width * drag.StartX));
        var startY = checked((int)Math.Round(bounds.Top + bounds.Height * drag.StartY));
        var endX = checked((int)Math.Round(bounds.Left + bounds.Width * drag.EndX));
        var endY = checked((int)Math.Round(bounds.Top + bounds.Height * drag.EndY));
        if (!SetCursorPos(startX, startY))
            throw new InvalidOperationException("작업 영역의 시작점으로 마우스 포인터를 이동하지 못했습니다.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            for (var step = 1; step <= 12; step++)
            {
                var x = startX + (endX - startX) * step / 12;
                var y = startY + (endY - startY) * step / 12;
                if (!SetCursorPos(x, y))
                    throw new InvalidOperationException("작업 영역 드래그 경로로 마우스 포인터를 이동하지 못했습니다.");
                Thread.Sleep(25);
            }
        }
        finally
        {
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static void VisualDrag(
        AutomationElement window,
        SemanticVisualAnchor anchor,
        SemanticRelativePoint endPoint)
    {
        if (window.Current.ControlType != ControlType.Window)
            throw new InvalidOperationException("TARGET_MISMATCH: 시각 대상 드래그 컨테이너가 창이 아닙니다.");
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var match = WindowsVisualAnchor.FindUnique(handle, anchor);
        if (match.CandidateCount != 1)
            throw new InvalidOperationException(
                $"TARGET_MISMATCH: 현재 화면의 시각 대상 후보가 {match.CandidateCount}개입니다.");
        var bounds = window.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
            throw new InvalidOperationException("TARGET_MISMATCH: 시각 대상 창 경계를 확인할 수 없습니다.");
        var endX = checked((int)Math.Round(bounds.Left + bounds.Width * endPoint.X));
        var endY = checked((int)Math.Round(bounds.Top + bounds.Height * endPoint.Y));
        if (!SetCursorPos(match.ScreenX, match.ScreenY))
            throw new InvalidOperationException("시각 대상 시작점으로 마우스 포인터를 이동하지 못했습니다.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            for (var step = 1; step <= 12; step++)
            {
                var x = match.ScreenX + (endX - match.ScreenX) * step / 12;
                var y = match.ScreenY + (endY - match.ScreenY) * step / 12;
                if (!SetCursorPos(x, y))
                    throw new InvalidOperationException("시각 대상 드래그 경로로 마우스 포인터를 이동하지 못했습니다.");
                Thread.Sleep(25);
            }
        }
        finally
        {
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static void VisualClick(AutomationElement window, SemanticVisualAnchor anchor)
    {
        if (window.Current.ControlType != ControlType.Window)
            throw new InvalidOperationException("TARGET_MISMATCH: 시각 클릭 컨테이너가 창이 아닙니다.");
        var match = WindowsVisualAnchor.FindUnique(new IntPtr(window.Current.NativeWindowHandle), anchor);
        if (match.CandidateCount != 1)
            throw new InvalidOperationException(
                $"TARGET_MISMATCH: 현재 화면의 시각 클릭 후보가 {match.CandidateCount}개입니다.");
        if (!SetCursorPos(match.ScreenX, match.ScreenY))
            throw new InvalidOperationException("시각 클릭 대상으로 마우스 포인터를 이동하지 못했습니다.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    private static bool TryGetListItemOrdinal(AutomationElement element, out int ordinal)
    {
        ordinal = -1;
        if (element.Current.ControlType != ControlType.ListItem) return false;
        var runtime = RuntimeIdentity(element);
        var siblings = ListItemsFor(element);
        ordinal = Array.FindIndex(siblings,
            item => string.Equals(RuntimeIdentity(item), runtime, StringComparison.Ordinal));
        return ordinal >= 0;
    }

    private static AutomationElement[] ListItemsFor(AutomationElement element)
    {
        var list = NearestList(element)
            ?? throw new InvalidOperationException("TARGET_MISMATCH: 목록 항목의 컨테이너를 확인할 수 없습니다.");
        if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
        {
            var scroll = ((ScrollPattern)scrollPattern).Current;
            if (scroll.VerticallyScrollable || scroll.HorizontallyScrollable)
                throw new InvalidOperationException("TARGET_MISMATCH: 일부 항목만 표시된 스크롤 목록의 전체 순서는 안전하게 확인할 수 없습니다.");
        }
        var listRuntime = RuntimeIdentity(list);
        return list.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .Where(item => NearestList(item) is { } owner
                && string.Equals(RuntimeIdentity(owner), listRuntime, StringComparison.Ordinal))
            .ToArray();
    }

    private static AutomationElement? NearestList(AutomationElement element)
    {
        var current = TreeWalker.ControlViewWalker.GetParent(element);
        while (current is not null)
        {
            if (current.Current.ControlType == ControlType.List) return current;
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        return null;
    }

    private static void SelectUniqueOption(AutomationElement element, string option)
    {
        ExpandCollapsePattern? expansion = null;
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
        {
            expansion = (ExpandCollapsePattern)expandPattern;
            if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                expansion.Expand();
                Thread.Sleep(150);
            }
        }
        try
        {
            var candidates = element.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Where(item => item.Current.IsEnabled
                    && string.Equals((item.Current.Name ?? string.Empty).Trim(), option.Trim(), StringComparison.OrdinalIgnoreCase)
                    && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                .Take(2)
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"TARGET_MISMATCH: 선택 항목이 {candidates.Length}개입니다.");
            if (!candidates[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                throw new InvalidOperationException("TARGET_MISMATCH: 항목을 의미적으로 선택할 수 없습니다.");
            ((SelectionItemPattern)selection).Select();
        }
        finally
        {
            if (expansion is not null && expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                expansion.Collapse();
        }
    }

    private static bool IsActionablePointerTarget(AutomationElement element)
    {
        var role = Role(element);
        if (role == "Slider" && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out _)) return true;
        return element.TryGetCurrentPattern(InvokePattern.Pattern, out _)
            || element.TryGetCurrentPattern(TogglePattern.Pattern, out _)
            || element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
            || element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
    }

    private static bool SameProcess(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
