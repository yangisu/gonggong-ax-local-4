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

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public GoogleSheetsObservation Observe()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return new GoogleSheetsObservation(string.Empty, [], [], 0);
        var root = AutomationElement.FromHandle(handle);
        if (root is null) return new GoogleSheetsObservation(string.Empty, [], [], 0);
        string process;
        try { process = Process.GetProcessById(root.Current.ProcessId).ProcessName; }
        catch { process = string.Empty; }

        var raw = Enumerate(root, 700).ToArray();
        var activeUrl = raw
            .Where(item => item.Current.ControlType == ControlType.Edit && !item.Current.IsPassword)
            .Select(ElementValue)
            .FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") ?? string.Empty;
        var tabs = raw
            .Where(item => item.Current.ControlType == ControlType.TabItem)
            .Select(item => new BrowserTab(ElementId(item), item.Current.Name ?? string.Empty, IsSelected(item) ? activeUrl : string.Empty, IsSelected(item)))
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
        return new GoogleSheetsObservation(process, tabs, elements, Revision(process, tabs));
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
        if (!element.Current.IsEnabled || element.Current.IsOffscreen || element.Current.IsPassword)
            throw new InvalidOperationException("BLOCKED: 현재 대상은 안전하게 실행할 수 없습니다.");
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
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
        try { return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) && ((SelectionItemPattern)pattern).Current.IsSelected; }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string ElementValue(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : string.Empty; }
        catch { return string.Empty; }
    }

    private static string ElementId(AutomationElement element)
    {
        var identity = $"{string.Join(".", element.GetRuntimeId())}|{element.Current.AutomationId}|{element.Current.ControlType.ProgrammaticName}|{element.Current.Name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }

    private static long Revision(string process, IReadOnlyList<BrowserTab> tabs)
    {
        var content = process + "|" + string.Join("|", tabs.Where(item => item.Active).Select(item => $"{item.Id}:{item.Title}:{item.Url}"));
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0);
    }
}
