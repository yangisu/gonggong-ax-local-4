using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Series4.Desktop;

internal static class SemanticUiBridge
{
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    internal static object Observe(JsonElement command)
    {
        var maximum = command.TryGetProperty("max_elements", out var value)
            ? Math.Clamp(value.GetInt32(), 1, 500)
            : 300;
        var (window, root, processId, processName, title, bounds) = Foreground();
        var elements = Enumerate(root, maximum).Select(ElementPayload).ToArray();
        return new
        {
            foreground = new { process_id = processId, process_name = processName, title, bounds },
            elements,
        };
    }

    internal static object Focus(JsonElement command)
    {
        var processHint = Normalize(command.TryGetProperty("process_hint", out var processValue) ? processValue.GetString() : string.Empty);
        var titleHint = Normalize(command.TryGetProperty("title_hint", out var titleValue) ? titleValue.GetString() : string.Empty);
        var contentHints = command.TryGetProperty("content_hints", out var hintsValue) && hintsValue.ValueKind == JsonValueKind.Array
            ? hintsValue.EnumerateArray().Select(item => Normalize(item.GetString())).Where(item => !string.IsNullOrEmpty(item)).ToArray()
            : [];
        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        AutomationElement? best = null;
        AutomationElement? bestTab = null;
        var bestScore = -1;
        for (var index = 0; index < windows.Count; index++)
        {
            AutomationElement window;
            try { window = windows[index]; }
            catch (ElementNotAvailableException) { continue; }
            var processId = (uint)window.Current.ProcessId;
            if (processId == (uint)Environment.ProcessId || processId == MainWindow.BridgeParentPid) continue;
            string processName;
            try { processName = Normalize(Process.GetProcessById((int)processId).ProcessName); }
            catch { continue; }
            var title = Normalize(window.Current.Name ?? string.Empty);
            var processMatches = string.IsNullOrEmpty(processHint)
                || processName.Contains(processHint, StringComparison.Ordinal)
                || processHint.Contains(processName, StringComparison.Ordinal);
            if (!processMatches) continue;
            var score = string.IsNullOrEmpty(processHint) ? 0 : 10;
            var windowTitleMatches = !string.IsNullOrEmpty(titleHint)
                && (title.Contains(titleHint, StringComparison.Ordinal) || titleHint.Contains(title, StringComparison.Ordinal));
            if (windowTitleMatches) score += title == titleHint ? 9 : 5;
            AutomationElement? matchingTab = null;
            var matchingTabScore = 0;
            if (!string.IsNullOrEmpty(titleHint) || contentHints.Length > 0)
            {
                var tabs = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                for (var tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
                {
                    AutomationElement tab;
                    try { tab = tabs[tabIndex]; }
                    catch (ElementNotAvailableException) { continue; }
                    var tabName = Normalize(tab.Current.Name);
                    var titleMatches = !string.IsNullOrEmpty(titleHint) && (tabName.Contains(titleHint, StringComparison.Ordinal) || titleHint.Contains(tabName, StringComparison.Ordinal));
                    var contentMatches = contentHints.Any(hint => tabName.Contains(hint, StringComparison.Ordinal) || hint.Contains(tabName, StringComparison.Ordinal));
                    if (!titleMatches && !contentMatches) continue;
                    var tabScore = titleMatches
                        ? tabName == titleHint ? 9 : 5
                        : 4;
                    if (tabScore <= matchingTabScore) continue;
                    matchingTab = tab;
                    matchingTabScore = tabScore;
                }
                score += matchingTabScore;
            }
            if (!windowTitleMatches && matchingTab is null && (!string.IsNullOrEmpty(titleHint) || contentHints.Length > 0)) continue;
            if (score <= bestScore) continue;
            best = window;
            bestTab = matchingTab;
            bestScore = score;
        }
        if (best is null) return new { focused = false };
        var handle = new IntPtr(best.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero) return new { focused = false };
        ForceForeground(handle);
        if (bestTab is not null)
        {
            try
            {
                if (bestTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                {
                    ((SelectionItemPattern)selection).Select();
                    Thread.Sleep(250);
                }
            }
            catch (ElementNotAvailableException) { }
        }
        Thread.Sleep(250);
        GetWindowThreadProcessId(handle, out var selectedProcessId);
        string selectedProcessName;
        try { selectedProcessName = Process.GetProcessById((int)selectedProcessId).ProcessName; }
        catch { selectedProcessName = string.Empty; }
        return new { focused = GetForegroundWindow() == handle, process_id = selectedProcessId, process_name = selectedProcessName, title = best.Current.Name ?? string.Empty };
    }

    internal static object Execute(JsonElement command)
    {
        var (_, root, processId, _, title, _) = Foreground();
        var expectedProcess = command.GetProperty("expected_process_id").GetUInt32();
        var expectedTitle = command.GetProperty("expected_title").GetString() ?? string.Empty;
        if (processId != expectedProcess || !string.Equals(title, expectedTitle, StringComparison.Ordinal))
            throw new InvalidOperationException("STALE_ACTION: foreground window changed after observation.");

        var expected = command.GetProperty("element");
        var expectedId = expected.GetProperty("id").GetString() ?? string.Empty;
        var expectedName = expected.GetProperty("name").GetString() ?? string.Empty;
        var expectedRole = expected.GetProperty("role").GetString() ?? string.Empty;
        var expectedAutomationId = expected.GetProperty("automation_id").GetString() ?? string.Empty;
        var action = command.GetProperty("semantic_action").GetString();
        if (expectedAutomationId == "__vision__")
        {
            if (action is not ("click" or "toggle"))
                throw new InvalidOperationException("BLOCKED: vision-only targets support clicks only.");
            var expectedBounds = expected.GetProperty("bounds").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            if (expectedBounds.Length != 4) throw new InvalidOperationException("TARGET_MISMATCH: vision bounds are invalid.");
            var (_, _, _, _, _, foregroundBounds) = Foreground();
            var x = checked((int)Math.Round((expectedBounds[0] + expectedBounds[2]) / 2));
            var y = checked((int)Math.Round((expectedBounds[1] + expectedBounds[3]) / 2));
            if (x < foregroundBounds[0] || x > foregroundBounds[2] || y < foregroundBounds[1] || y > foregroundBounds[3])
                throw new InvalidOperationException("TARGET_MISMATCH: vision target is outside the current window.");
            SetCursorPos(x, y);
            mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            return new { execution_state = "confirmed", method = "fresh-vision-center-click", element_id = expectedId };
        }
        var element = Enumerate(root, 500).FirstOrDefault(item =>
            ElementId(item) == expectedId
            && string.Equals(item.Current.Name ?? string.Empty, expectedName, StringComparison.Ordinal)
            && string.Equals(item.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""), expectedRole, StringComparison.OrdinalIgnoreCase));
        if (element is null || !element.Current.IsEnabled || element.Current.IsOffscreen)
            throw new InvalidOperationException("TARGET_MISMATCH: semantic element is no longer current.");
        if (element.Current.IsPassword)
            throw new InvalidOperationException("BLOCKED: password controls cannot be automated.");

        var text = command.TryGetProperty("value", out var textValue) ? textValue.GetString() ?? string.Empty : string.Empty;
        string method;
        if (action is "type" or "select")
        {
            if (text.Length is < 1 or > 10000) throw new ArgumentException("Text length must be 1 to 10000 characters.");
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                throw new InvalidOperationException("TARGET_MISMATCH: target does not support semantic text entry.");
            ((ValuePattern)valuePattern).SetValue(text);
            method = "uia-value";
        }
        else if (action == "set-range")
        {
            if (!SemanticRangeValue.TryParse(text, out var rangeValue)
                || !element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangePattern))
                throw new InvalidOperationException("TARGET_MISMATCH: target does not support the requested range value.");
            var range = (RangeValuePattern)rangePattern;
            if (range.Current.IsReadOnly || rangeValue < range.Current.Minimum || rangeValue > range.Current.Maximum)
                throw new InvalidOperationException("TARGET_MISMATCH: range value is outside the current target bounds.");
            range.SetValue(rangeValue);
            method = "uia-range-value";
        }
        else if (action == "scroll")
        {
            if (!SemanticScrollCommand.TryParse(text, out var scrollCommand)
                || !element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
                throw new InvalidOperationException("TARGET_MISMATCH: target does not support the requested semantic scroll.");
            var scroll = (ScrollPattern)scrollPattern;
            var increment = scrollCommand.Direction == "increment" ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
            for (var index = 0; index < scrollCommand.Count; index++)
                scroll.Scroll(
                    scrollCommand.Axis == "horizontal" ? increment : ScrollAmount.NoAmount,
                    scrollCommand.Axis == "vertical" ? increment : ScrollAmount.NoAmount);
            method = "uia-scroll";
        }
        else if (action == "select-option")
        {
            if (text.Length is < 1 or > 1000)
                throw new ArgumentException("Selection value length must be 1 to 1000 characters.");
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
                var matches = Enumerate(element, 300).Where(item =>
                    string.Equals((item.Current.Name ?? string.Empty).Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)
                    && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)).Take(2).ToArray();
                if (matches.Length != 1
                    || !matches[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                    throw new InvalidOperationException($"TARGET_MISMATCH: semantic option count is {matches.Length}.");
                ((SelectionItemPattern)selection).Select();
                method = "uia-select-option";
            }
            finally
            {
                if (expansion is not null && expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    expansion.Collapse();
            }
        }
        else if (action == "focus")
        {
            element.SetFocus();
            method = "uia-focus";
        }
        else if (action == "reorder-item")
        {
            if (!SemanticOrdinalValue.TryParse(text, out var ordinal))
                throw new ArgumentException("List ordinal is invalid.");
            ReorderListItem(element, ordinal);
            method = "semantic-list-reorder";
        }
        else if (action == "toggle" && element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
        {
            ((TogglePattern)togglePattern).Toggle();
            method = "uia-toggle";
        }
        else if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            method = "uia-invoke";
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            method = "uia-select";
        }
        else
        {
            var rectangle = element.Current.BoundingRectangle;
            if (rectangle.IsEmpty || rectangle.Width <= 1 || rectangle.Height <= 1)
                throw new InvalidOperationException("TARGET_MISMATCH: target has no clickable area.");
            var x = checked((int)Math.Round(rectangle.Left + rectangle.Width / 2));
            var y = checked((int)Math.Round(rectangle.Top + rectangle.Height / 2));
            SetCursorPos(x, y);
            mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            method = "verified-center-click";
        }
        return new { execution_state = "confirmed", method, element_id = expectedId };
    }

    internal static object Capture(JsonElement command)
    {
        var (window, _, processId, processName, title, bounds) = Foreground();
        var bytes = CaptureJpeg(window, 75);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Captured screen exceeds 4 MiB.");
        return new { process_id = processId, process_name = processName, title, bounds, mime_type = "image/jpeg", data = Convert.ToBase64String(bytes) };
    }

    private static (IntPtr Window, AutomationElement Root, uint ProcessId, string ProcessName, string Title, double[] Bounds) Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rectangle))
            throw new InvalidOperationException("No foreground window is available.");
        GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId || processId == MainWindow.BridgeParentPid)
            throw new InvalidOperationException("The automation host cannot target itself.");
        var root = AutomationElement.FromHandle(window) ?? throw new InvalidOperationException("Foreground window does not expose UI Automation.");
        string processName;
        try { processName = Process.GetProcessById((int)processId).ProcessName; }
        catch { processName = string.Empty; }
        return (window, root, processId, processName, root.Current.Name ?? string.Empty,
            [rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom]);
    }

    private static IEnumerable<AutomationElement> Enumerate(AutomationElement root, int maximum)
    {
        var collection = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var accepted = 0;
        for (var index = 0; index < collection.Count && accepted < maximum; index++)
        {
            AutomationElement item;
            try { item = collection[index]; _ = item.Current.ControlType; }
            catch (ElementNotAvailableException) { continue; }
            var rectangle = item.Current.BoundingRectangle;
            if (rectangle.IsEmpty || rectangle.Width <= 1 || rectangle.Height <= 1) continue;
            accepted++;
            yield return item;
        }
    }

    private static object ElementPayload(AutomationElement element)
    {
        var rectangle = element.Current.BoundingRectangle;
        return new
        {
            id = ElementId(element),
            name = element.Current.Name ?? string.Empty,
            role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            automation_id = element.Current.AutomationId ?? string.Empty,
            value = ElementValue(element),
            enabled = element.Current.IsEnabled,
            offscreen = element.Current.IsOffscreen,
            password = element.Current.IsPassword,
            keyboard_focused = element.Current.HasKeyboardFocus,
            patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray(),
            bounds = new[] { rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom },
        };
    }

    private static string ElementId(AutomationElement element)
    {
        var runtime = string.Join(".", element.GetRuntimeId());
        var identity = $"{runtime}|{element.Current.AutomationId}|{element.Current.ControlType.ProgrammaticName}|{element.Current.Name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }

    private static string Normalize(string? value)
    {
        var normalized = new string((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalized.EndsWith("exe", StringComparison.Ordinal) ? normalized[..^3] : normalized;
    }

    private static void ForceForeground(IntPtr target)
    {
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            if (IsIconic(target)) ShowWindow(target, 9);
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static string ElementValue(AutomationElement element)
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

    private static void ReorderListItem(AutomationElement element, int finalOrdinal)
    {
        var siblings = ListItemsFor(element);
        if (siblings.Length < 2 || finalOrdinal >= siblings.Length)
            throw new InvalidOperationException("TARGET_MISMATCH: list ordinal is outside the current range.");
        var runtime = RuntimeIdentity(element);
        var currentOrdinal = Array.FindIndex(siblings,
            item => string.Equals(RuntimeIdentity(item), runtime, StringComparison.Ordinal));
        if (currentOrdinal < 0 || currentOrdinal == finalOrdinal)
            throw new InvalidOperationException("TARGET_MISMATCH: list item is missing or already at the requested ordinal.");
        var destination = siblings[finalOrdinal];
        if (destination.Current.IsOffscreen)
        {
            if (!destination.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
                throw new InvalidOperationException("TARGET_MISMATCH: destination list item cannot be revealed.");
            ((ScrollItemPattern)scroll).ScrollIntoView();
            Thread.Sleep(150);
        }
        var sourceBounds = element.Current.BoundingRectangle;
        var destinationBounds = destination.Current.BoundingRectangle;
        if (sourceBounds.IsEmpty || destinationBounds.IsEmpty
            || sourceBounds.Width <= 1 || sourceBounds.Height <= 1
            || destinationBounds.Width <= 1 || destinationBounds.Height <= 1)
            throw new InvalidOperationException("TARGET_MISMATCH: list reorder bounds are unavailable.");
        var startX = checked((int)Math.Round(sourceBounds.Left + sourceBounds.Width / 2));
        var startY = checked((int)Math.Round(sourceBounds.Top + sourceBounds.Height / 2));
        var endX = checked((int)Math.Round(destinationBounds.Left + destinationBounds.Width / 2));
        var endY = checked((int)Math.Round(destinationBounds.Top + destinationBounds.Height / 2));
        if (!SetCursorPos(startX, startY))
            throw new InvalidOperationException("Could not position the pointer on the list item.");
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            for (var step = 1; step <= 10; step++)
            {
                var x = startX + (endX - startX) * step / 10;
                var y = startY + (endY - startY) * step / 10;
                if (!SetCursorPos(x, y))
                    throw new InvalidOperationException("Could not move the pointer along the list reorder path.");
                Thread.Sleep(25);
            }
        }
        finally
        {
            mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        }
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
            ?? throw new InvalidOperationException("TARGET_MISMATCH: list container is unavailable.");
        if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
        {
            var scroll = ((ScrollPattern)scrollPattern).Current;
            if (scroll.VerticallyScrollable || scroll.HorizontallyScrollable)
                throw new InvalidOperationException("TARGET_MISMATCH: the complete order of a scrollable list is not safely observable.");
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

    private static string RuntimeIdentity(AutomationElement element) =>
        string.Join('.', element.GetRuntimeId());

    private static byte[] CaptureJpeg(IntPtr window, int quality)
    {
        if (!GetWindowRect(window, out var rectangle)) throw new InvalidOperationException("Could not read foreground bounds.");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width is < 1 or > 10000 || height is < 1 or > 10000) throw new InvalidOperationException("Foreground bounds are invalid.");
        var source = GetWindowDC(window);
        var target = CreateCompatibleDC(source);
        var bitmap = CreateCompatibleBitmap(source, width, height);
        var previous = SelectObject(target, bitmap);
        try
        {
            if (!BitBlt(target, 0, 0, width, height, source, 0, 0, Srccopy))
                throw new InvalidOperationException("Screen capture failed.");
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)pixels.Length } };
            if (GetDIBits(target, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) == 0)
                throw new InvalidOperationException("Screen pixels could not be read.");
            var sourceImage = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(sourceImage));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            SelectObject(target, previous);
            DeleteObject(bitmap);
            DeleteDC(target);
            ReleaseDC(window, source);
        }
    }
}
