using System.Diagnostics;
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
                element.Password)).ToArray());
        return (frame, target);
    }

    public static (SemanticDemonstrationFrame Frame, SemanticTargetSelector? Target) CapturePointDemonstrationFrame(
        string id,
        double offsetSeconds,
        double screenX,
        double screenY)
    {
        var root = ForegroundRoot();
        var processId = root.Current.ProcessId;
        if (processId == Environment.ProcessId || processId == (int)MainWindow.BridgeParentPid)
            throw new InvalidOperationException("자동화 호스트 자체는 시연 대상으로 사용할 수 없습니다.");
        string process;
        try { process = Process.GetProcessById(processId).ProcessName; }
        catch { process = string.Empty; }
        var element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        SemanticTargetSelector? target = null;
        SemanticElementEvidence[] elements = [];
        if (element is not null
            && element.Current.ProcessId == processId
            && !element.Current.IsPassword
            && !string.IsNullOrWhiteSpace(element.Current.Name))
        {
            var automationId = element.Current.AutomationId ?? string.Empty;
            target = new SemanticTargetSelector(
                [Role(element)],
                element.Current.Name,
                string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                element.Current.IsOffscreen);
            elements =
            [
                new SemanticElementEvidence(
                    Role(element), element.Current.Name, automationId, ReadValue(element),
                    element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.IsPassword),
            ];
        }
        return (
            new SemanticDemonstrationFrame(
                id, offsetSeconds, process, root.Current.Name ?? string.Empty, string.Empty, elements),
            target);
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
            case "toggle":
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
                    ((TogglePattern)togglePattern).Toggle();
                else
                    InvokeOrClick(element);
                break;
            case "click":
                InvokeOrClick(element);
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
        var elements = Enumerate(root, 600).Select(ToElement).ToArray();
        var url = ReadActiveUrl(root);
        return new SemanticWorkflowObservation(process, title, url, elements, Revision(process, title, url, elements));
    }

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
        var candidates = Enumerate(root, 800).Where(element =>
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
                        || string.Equals(selector.ExpectedValue, ReadValue(element), StringComparison.Ordinal));
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

    private static SemanticWorkflowElement ToElement(AutomationElement element)
    {
        var role = Role(element);
        var name = element.Current.Name ?? string.Empty;
        var automationId = element.Current.AutomationId ?? string.Empty;
        var runtime = string.Join('.', element.GetRuntimeId());
        var idSource = $"{runtime}|{role}|{name}|{automationId}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource))).ToLowerInvariant()[..24];
        return new SemanticWorkflowElement(
            id, role, name, automationId, ReadValue(element),
            element.Current.IsEnabled, element.Current.IsOffscreen, element.Current.IsPassword);
    }

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
            source.Append('\u001e').Append(element.Id).Append('|').Append(element.Value).Append('|').Append(element.Offscreen);
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

    private static bool SameProcess(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
