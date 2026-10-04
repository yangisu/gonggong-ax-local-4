using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace SemanticWorkflowFixture;

public static class Program
{
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseWheel = 0x0800;
    private const uint KeyUp = 0x0002;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    [STAThread]
    public static void Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "note";
        var application = new Application();
        var selfDemonstrating = mode is "editor-demo" or "scroll-demo" or "keyboard-demo";
        Button? newNoteButton = null;
        var content = mode switch
        {
            "settings" or "keyboard-demo" => SettingsContent(),
            "scroll" or "scroll-demo" => ScrollContent(),
            "editor" or "editor-demo" => NoteEditorContent(),
            _ => NoteStartContent(out newNoteButton),
        };
        var window = new Window
        {
            Title = "Semantic Workflow UX Fixture",
            Width = 640,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            Content = content,
        };
        if (selfDemonstrating)
        {
            window.Loaded += async (_, _) =>
            {
                await Task.Delay(1000);
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                if (mode == "scroll-demo")
                {
                    var list = FindDescendant<ListBox>(window)
                        ?? throw new InvalidOperationException("업무 목록을 찾지 못했습니다.");
                    var point = list.PointToScreen(new Point(list.ActualWidth / 2, list.ActualHeight / 2));
                    SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
                    await Task.Delay(300);
                    await Task.Run(() =>
                    {
                        mouse_event(MouseWheel, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
                        Thread.Sleep(500);
                        mouse_event(MouseWheel, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
                    });
                    return;
                }
                if (mode == "keyboard-demo")
                {
                    var toggle = FindDescendant<CheckBox>(window)
                        ?? throw new InvalidOperationException("어두운 모드 설정을 찾지 못했습니다.");
                    toggle.Focus();
                    await Task.Delay(300);
                    await Task.Run(() =>
                    {
                        keybd_event(0x20, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(120);
                        keybd_event(0x20, 0, KeyUp, UIntPtr.Zero);
                    });
                    return;
                }
                var editor = FindDescendant<TextBox>(window)
                    ?? throw new InvalidOperationException("메모 편집기를 찾지 못했습니다.");
                editor.Focus();
                await Task.Delay(300);
                await Task.Run(() => TypeAscii("meeting"));
            };
        }
        application.Run(window);
    }

    private static UIElement NoteStartContent(out Button button)
    {
        var panel = Panel("메모 업무");
        var createdButton = Named(new Button { Content = "새 메모", Width = 180, Height = 44, HorizontalAlignment = HorizontalAlignment.Left }, "new-note", "새 메모");
        button = createdButton;
        createdButton.Click += (_, _) =>
        {
            var owner = Window.GetWindow(createdButton);
            if (owner is not null) owner.Content = NoteEditorContent();
        };
        panel.Children.Add(createdButton);
        return panel;
    }

    private static void Click(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static void TypeAscii(string text)
    {
        foreach (var character in text)
        {
            var virtualKey = checked((byte)char.ToUpperInvariant(character));
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            Thread.Sleep(120);
            keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
            Thread.Sleep(180);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static UIElement NoteEditorContent()
    {
        var panel = Panel("새 메모 편집 중");
        var editor = Named(new TextBox
        {
            Width = 440,
            Height = 120,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        }, "note-editor", "메모 내용");
        var status = Named(new TextBlock { Text = "입력 대기", FontSize = 18 }, "note-status", "입력 대기");
        editor.TextChanged += (_, _) =>
        {
            status.Text = string.IsNullOrEmpty(editor.Text) ? "입력 대기" : "저장됨";
            AutomationProperties.SetName(status, status.Text);
        };
        panel.Children.Add(editor);
        panel.Children.Add(status);
        return panel;
    }

    private static UIElement SettingsContent()
    {
        var panel = Panel("화면 설정");
        var toggle = Named(new CheckBox { Content = "어두운 모드", FontSize = 20 }, "dark-mode", "어두운 모드");
        var status = Named(new TextBlock { Text = "밝은 모드 사용 중", FontSize = 18 }, "mode-status", "밝은 모드 사용 중");
        toggle.Checked += (_, _) =>
        {
            status.Text = "어두운 모드 사용 중";
            AutomationProperties.SetName(status, status.Text);
        };
        toggle.Unchecked += (_, _) =>
        {
            status.Text = "밝은 모드 사용 중";
            AutomationProperties.SetName(status, status.Text);
        };
        panel.Children.Add(toggle);
        panel.Children.Add(status);
        return panel;
    }

    private static UIElement ScrollContent()
    {
        var panel = Panel("업무 목록");
        var list = Named(new ListBox
        {
            Width = 440,
            Height = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = Enumerable.Range(1, 40).Select(index => $"업무 항목 {index}").ToArray(),
        }, "work-list", "업무 목록");
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
        var status = Named(new TextBlock { Text = "목록 시작", FontSize = 18 }, "scroll-status", "목록 시작");
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) =>
        {
            if (args.VerticalOffset <= 0) return;
            status.Text = "아래 항목 표시됨";
            AutomationProperties.SetName(status, status.Text);
        }));
        panel.Children.Add(list);
        panel.Children.Add(status);
        return panel;
    }

    private static StackPanel Panel(string heading)
    {
        var panel = new StackPanel { Margin = new Thickness(42) };
        panel.Children.Add(Named(new TextBlock
        {
            Text = heading,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 28),
        }, "heading", heading));
        return panel;
    }

    private static T Named<T>(T element, string automationId, string name) where T : UIElement
    {
        AutomationProperties.SetAutomationId(element, automationId);
        AutomationProperties.SetName(element, name);
        if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 0, 0, 20);
        return element;
    }
}
