using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SemanticWorkflowFixture;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "note";
        var application = new Application();
        var window = new Window
        {
            Title = "Semantic Workflow UX Fixture",
            Width = 640,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            Content = mode == "settings" ? SettingsContent() : NoteStartContent(),
        };
        application.Run(window);
    }

    private static UIElement NoteStartContent()
    {
        var panel = Panel("메모 업무");
        var button = Named(new Button { Content = "새 메모", Width = 180, Height = 44, HorizontalAlignment = HorizontalAlignment.Left }, "new-note", "새 메모");
        button.Click += (_, _) =>
        {
            var owner = Window.GetWindow(button);
            if (owner is not null) owner.Content = NoteEditorContent();
        };
        panel.Children.Add(button);
        return panel;
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
