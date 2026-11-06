using System.Windows;
using System.Windows.Threading;
using PinyinTranslate.Update;

namespace PinyinTranslate.App;

internal sealed class DesktopUpdateLifecycle(
    IAppServices services, Dispatcher dispatcher, Func<bool> canRestart, Func<bool> isClosing) : IUpdateLifecycle
{
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isClosing() || !canRestart()) throw new InvalidOperationException("请先完成截图或保存设置，再更新并重启。");
            await services.PrepareForUpdateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isClosing() || !canRestart()) throw new InvalidOperationException("设置有未保存的修改，已取消重启。");
        }).Task.Unwrap();
    }

    public async Task ResumeAsync()
    {
        if (isClosing() || dispatcher.HasShutdownStarted) return;
        await dispatcher.InvokeAsync(async () =>
        {
            if (!isClosing()) await services.ResumeAfterUpdateFailureAsync();
        }).Task.Unwrap();
    }

    public void Exit()
    {
        // The SDK handoff must not call Process.Exit: normal shutdown releases hooks and the mutex.
        if (!isClosing() && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => { if (!isClosing()) Application.Current.Shutdown(); }, DispatcherPriority.Normal);
    }
}
