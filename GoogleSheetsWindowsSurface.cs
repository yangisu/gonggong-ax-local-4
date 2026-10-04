using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace Series4.Desktop;

public sealed class GoogleSheetsWindowsSurface : IGoogleSheetsSurface
{
    private const byte VkControl = 0x11;
    private const byte VkW = 0x57;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static IReadOnlyList<int> SetForegroundChromeBounds(int x, int y, int width, int height)
    {
        if (x is < -2000 or > 8000 || y is < -2000 or > 8000 || width is < 800 or > 2400 || height is < 600 or > 1600)
            throw new ArgumentOutOfRangeException(nameof(width), "통합 테스트 창 범위가 허용 범위를 벗어났습니다.");
        var handle = GetForegroundWindow();
        var root = handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
        if (root is null) throw new InvalidOperationException("전경 창을 관찰할 수 없습니다.");
        string process;
        try { process = Process.GetProcessById(root.Current.ProcessId).ProcessName; }
        catch { process = string.Empty; }
        if (!process.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("통합 테스트 창 배치는 전경 Chrome에만 허용됩니다.");
        ShowWindow(handle, 9);
        if (!SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpShowWindow))
            throw new InvalidOperationException("Chrome 창 위치와 크기를 적용하지 못했습니다.");
        Thread.Sleep(250);
        if (!GetWindowRect(handle, out var actual))
            throw new InvalidOperationException("적용된 Chrome 창 범위를 읽지 못했습니다.");
        var actualWidth = actual.Right - actual.Left;
        var actualHeight = actual.Bottom - actual.Top;
        if (Math.Abs(actual.Left - x) > 20 || Math.Abs(actual.Top - y) > 20 || Math.Abs(actualWidth - width) > 40 || Math.Abs(actualHeight - height) > 40)
            throw new InvalidOperationException("Chrome 창 범위가 요청한 변형 조건과 일치하지 않습니다.");
        return [actual.Left, actual.Top, actual.Right, actual.Bottom];
    }

    public GoogleSheetsObservation Observe()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { return ObserveOnce(); }
            catch (ElementNotAvailableException error) { last = error; }
            catch (COMException error) { last = error; }
            catch (InvalidOperationException error) { last = error; }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("Chrome 접근성 상태가 관찰 중 계속 변경되었습니다.", last);
    }

    private static GoogleSheetsObservation ObserveOnce()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return new GoogleSheetsObservation(string.Empty, [], [], 0);
        var root = AutomationElement.FromHandle(handle);
        if (root is null) return new GoogleSheetsObservation(string.Empty, [], [], 0);
        var windowTitle = root.Current.Name ?? string.Empty;
        string process;
        try { process = Process.GetProcessById(root.Current.ProcessId).ProcessName; }
        catch { process = string.Empty; }

        var raw = Enumerate(root, 700).ToArray();
        var activeUrl = raw
            .Where(item => item.Current.ControlType == ControlType.Document
                && string.Equals(item.Current.AutomationId, "RootWebArea", StringComparison.Ordinal))
            .Select(ElementValue)
            .FirstOrDefault(IsHttpUrl) ?? string.Empty;
        if (string.IsNullOrEmpty(activeUrl))
        {
            var addressValue = raw
                .Where(item => item.Current.ControlType == ControlType.Edit && !item.Current.IsPassword
                    && (item.Current.Name.Contains("주소", StringComparison.OrdinalIgnoreCase)
                        || item.Current.Name.Contains("address", StringComparison.OrdinalIgnoreCase)
                        || item.Current.AutomationId == "view_1012"))
                .Select(ElementValue)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(addressValue))
                activeUrl = IsHttpUrl(addressValue) ? addressValue : $"https://{addressValue.TrimStart('/')}";
        }
        var tabElements = raw.Where(item => item.Current.ControlType == ControlType.TabItem).ToArray();
        var selectedIds = tabElements.Where(IsSelected).Select(ElementId).ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count != 1)
        {
            var titleMatches = tabElements
                .Where(item => WindowTitleMatchesTab(windowTitle, item.Current.Name ?? string.Empty))
                .Select(ElementId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (titleMatches.Length == 1)
            {
                selectedIds.Clear();
                selectedIds.Add(titleMatches[0]);
            }
        }
        var tabs = tabElements
            .Select(item =>
            {
                var id = ElementId(item);
                var selected = selectedIds.Count == 1 && selectedIds.Contains(id);
                return new BrowserTab(id, item.Current.Name ?? string.Empty, selected ? activeUrl : string.Empty, selected);
            })
            .ToList();
        if (tabs.Count == 0)
            tabs.Add(new BrowserTab("active-window", root.Current.Name ?? string.Empty, activeUrl, true));

        var elements = raw.Select(item => new SemanticElement(
            ElementId(item),
            item.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty).ToLowerInvariant(),
            item.Current.Name ?? string.Empty,
            item.Current.IsEnabled,
            item.Current.IsOffscreen,
            item.Current.IsPassword)).ToArray();
        return new GoogleSheetsObservation(process, tabs, elements, Revision(process, tabs), windowTitle);
    }

    public void Execute(GoldenPathAction action, GoogleSheetsObservation observation)
    {
        var current = Observe();
        if (current.Revision != observation.Revision)
            throw new InvalidOperationException("STALE_ACTION: 화면이 관찰 후 변경되었습니다.");
        if (action.Kind == "close_active_tab")
        {
            if (GoogleSheetsGoldenPathRunner.Classify(current) != GoogleSheetsState.BlankSpreadsheetOpen)
                throw new InvalidOperationException("TARGET_MISMATCH: 현재 탭이 생성된 스프레드시트로 검증되지 않았습니다.");
            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
            Thread.Sleep(500);
            return;
        }

        if (action.Kind != "click" || string.IsNullOrWhiteSpace(action.TargetId))
            throw new InvalidOperationException("BLOCKED: 지원되지 않는 골든 패스 행동입니다.");
        var root = AutomationElement.FromHandle(GetForegroundWindow()) ?? throw new InvalidOperationException("전경 창을 다시 관찰할 수 없습니다.");
        var matches = Enumerate(root, 700).Where(item => ElementId(item) == action.TargetId).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("TARGET_MISMATCH: 대상이 사라졌거나 복수로 관찰되었습니다.");
        var element = matches[0];
        if (!element.Current.IsEnabled || element.Current.IsPassword)
            throw new InvalidOperationException("BLOCKED: 현재 대상은 안전하게 실행할 수 없습니다.");
        if (element.Current.IsOffscreen)
        {
            if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItem))
                throw new InvalidOperationException("TARGET_MISMATCH: 대상이 화면 밖에 있고 의미 스크롤을 지원하지 않습니다.");
            ((ScrollItemPattern)scrollItem).ScrollIntoView();
            Thread.Sleep(250);
            root = AutomationElement.FromHandle(GetForegroundWindow()) ?? throw new InvalidOperationException("스크롤 후 전경 창을 다시 관찰할 수 없습니다.");
            var refreshed = Enumerate(root, 700).Where(item => ElementId(item) == action.TargetId).ToArray();
            if (refreshed.Length != 1 || refreshed[0].Current.IsOffscreen)
                throw new InvalidOperationException("TARGET_MISMATCH: 스크롤 후 대상을 유일하게 검증할 수 없습니다.");
            element = refreshed[0];
        }
        var pointerPreferred = action.Kind == "click"
            && element.Current.ControlType is var controlType
            && (controlType == ControlType.ListItem || controlType == ControlType.Custom);
        if (!pointerPreferred && element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else if (action.Kind == "select" && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            ((SelectionItemPattern)select).Select();
        else
        {
            var rectangle = element.Current.BoundingRectangle;
            if (rectangle.IsEmpty || rectangle.Width < 2 || rectangle.Height < 2)
                throw new InvalidOperationException("TARGET_MISMATCH: 클릭 가능한 영역이 없습니다.");
            SetCursorPos((int)Math.Round(rectangle.Left + rectangle.Width / 2), (int)Math.Round(rectangle.Top + rectangle.Height / 2));
            mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        }
        Thread.Sleep(750);
    }

    private static IEnumerable<AutomationElement> Enumerate(AutomationElement root, int maximum)
    {
        var collection = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var count = 0;
        for (var index = 0; index < collection.Count && count < maximum; index++)
        {
            AutomationElement item;
            try { item = collection[index]; _ = item.Current.ControlType; }
            catch (ElementNotAvailableException) { continue; }
            count++;
            yield return item;
        }
    }

    private static bool IsSelected(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)
                && ((SelectionItemPattern)selection).Current.IsSelected)
                return true;
            return element.Current.HasKeyboardFocus;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool WindowTitleMatchesTab(string windowTitle, string tabTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || string.IsNullOrWhiteSpace(tabTitle)) return false;
        var normalizedWindow = windowTitle.Trim();
        var normalizedTab = NormalizeTabTitle(tabTitle);
        return string.Equals(normalizedWindow, normalizedTab, StringComparison.OrdinalIgnoreCase)
            || normalizedWindow.StartsWith(normalizedTab + " - ", StringComparison.OrdinalIgnoreCase)
            || normalizedWindow.StartsWith(normalizedTab + " – ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTabTitle(string tabTitle)
    {
        var normalizedTab = tabTitle.Trim();
        foreach (var suffix in new[] { " - 메모리 사용량", " - Memory usage" })
        {
            var suffixIndex = normalizedTab.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (suffixIndex > 0) normalizedTab = normalizedTab[..suffixIndex].TrimEnd();
        }
        return normalizedTab;
    }

    private static string ElementValue(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string ElementId(AutomationElement element)
    {
        var identity = $"{string.Join(".", element.GetRuntimeId())}|{element.Current.AutomationId}|{element.Current.ControlType.ProgrammaticName}|{element.Current.Name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }

    private static long Revision(string process, IReadOnlyList<BrowserTab> tabs)
    {
        var content = process + "|" + string.Join("|", tabs.Where(item => item.Active).Select(item => $"{NormalizeTabTitle(item.Title)}:{item.Url}"));
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0);
    }
}
