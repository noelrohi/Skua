// PROTOTYPE (#6): headless stand-ins for the Windows-only services the Engine resolves.
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;
using System.Reflection;

namespace BridgeConsole;

public class HeadlessSettings : ISettingsService
{
    private readonly UnifiedSettingsService _s = new();
    public HeadlessSettings() => _s.Initialize(AppRole.Client);
    public T? Get<T>(string key) => _s.Get<T>(key);
    public T Get<T>(string key, T defaultValue) => _s.Get(key, defaultValue);
    public void Set<T>(string key, T value) => _s.Set(key, value);
    public void Initialize(AppRole role) => _s.Initialize(role);
    public SharedSettings GetShared() => _s.GetShared();
    public ClientSettings GetClient() => _s.GetClient();
    public ManagerSettings GetManager() => _s.GetManager();
    public void SetApplicationVersion() => _s.SetApplicationVersion();
}

public class HeadlessDispatcher : IDispatcherService
{
    public void Invoke(Action action) => action();
}

/// <summary>#10/#9 fallback: nothing answers, so Notices return immediately and Questions are Cancelled.</summary>
public class HeadlessDialogs : IDialogService
{
    public static Action<string>? Log;
    public void ShowMessageBox(string message, string caption) => Log?.Invoke($"[notice] {caption}: {message}");
    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        Log?.Invoke($"[question->cancel] {caption}: {message}");
        return null;
    }
    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        Log?.Invoke($"[question->cancel] {caption}: {message} [{string.Join("|", buttons)}]");
        return DialogResult.Cancelled;
    }
    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => null;
    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => null;
    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class => null;
}

/// <summary>Returns default(T) for every member; for services the headless run never meaningfully uses.</summary>
public class Noop<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, Noop<T>>();
    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        var rt = m!.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
