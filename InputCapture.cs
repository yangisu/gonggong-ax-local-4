using SharpHook;

namespace Series4.Desktop;

public interface IInputCapture : IDisposable
{
    event EventHandler<HookEventArgs>? HookEnabled;
    event EventHandler<MouseHookEventArgs>? MousePressed;
    event EventHandler<MouseHookEventArgs>? MouseReleased;
    event EventHandler<MouseHookEventArgs>? MouseDragged;
    event EventHandler<MouseWheelHookEventArgs>? MouseWheel;
    event EventHandler<KeyboardHookEventArgs>? KeyPressed;
    event EventHandler<KeyboardHookEventArgs>? KeyReleased;

    Task RunAsync();

    void Stop();
}

public sealed class SharpHookInputCapture : IInputCapture
{
    private readonly SimpleGlobalHook hook = new(
        runAsyncOnBackgroundThread: true
    );

    public event EventHandler<HookEventArgs>? HookEnabled
    {
        add => hook.HookEnabled += value;
        remove => hook.HookEnabled -= value;
    }

    public event EventHandler<MouseHookEventArgs>? MousePressed
    {
        add => hook.MousePressed += value;
        remove => hook.MousePressed -= value;
    }

    public event EventHandler<MouseHookEventArgs>? MouseReleased
    {
        add => hook.MouseReleased += value;
        remove => hook.MouseReleased -= value;
    }

    public event EventHandler<MouseHookEventArgs>? MouseDragged
    {
        add => hook.MouseDragged += value;
        remove => hook.MouseDragged -= value;
    }

    public event EventHandler<MouseWheelHookEventArgs>? MouseWheel
    {
        add => hook.MouseWheel += value;
        remove => hook.MouseWheel -= value;
    }

    public event EventHandler<KeyboardHookEventArgs>? KeyPressed
    {
        add => hook.KeyPressed += value;
        remove => hook.KeyPressed -= value;
    }

    public event EventHandler<KeyboardHookEventArgs>? KeyReleased
    {
        add => hook.KeyReleased += value;
        remove => hook.KeyReleased -= value;
    }

    public Task RunAsync() => hook.RunAsync();

    public void Stop() => hook.Stop();

    public void Dispose() => hook.Dispose();
}
