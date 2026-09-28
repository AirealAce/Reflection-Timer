using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Desktop;

// A real browser editor on a separate Windows input queue. Checking only a
// top-level foreground HWND misses a lost renderer/child-window input focus.
internal sealed class BrowserFocusTarget : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form window = null!;
    private WebView2 browser = null!;
    internal BrowserFocusTarget()
    {
        thread = new Thread(() => {
            using var form = new Form { Text = "Synthetic browser editor — focus regression", Size = new(560, 240) };
            window = form;
            using var view = new WebView2 { Dock = DockStyle.Fill };
            browser = view;
            form.Controls.Add(view);
            form.Shown += async (_, _) => {
                try {
                    await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null,
                        Path.Combine(Path.GetTempPath(), "ReflectionTimer-FocusEditor-" + Guid.NewGuid().ToString("N"))));
                    var loaded = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
                    view.NavigateToString("<label for='editor'>Synthetic editor (no user data)</label><textarea id='editor' style='width:90%;height:100px'>focus test</textarea>");
                    await loaded.Task;
                    ready.TrySetResult();
                } catch (Exception error) { ready.TrySetException(error); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    internal Task Ready => ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    private Task<T> OnThread<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.BeginInvoke(async () => {
            try { result.SetResult(await action()); } catch (Exception error) { result.SetException(error); }
        });
        return result.Task;
    }
    internal async Task FocusAsync()
    {
        await Ready;
        await OnThread(async () => {
            WindowActivation.Focus(window); browser.Focus();
            return await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor').focus();document.querySelector('#editor').setSelectionRange(3,3)");
        });
    }
    internal Task<string> StateAsync() => OnThread(async () => {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var valid = GetGUIThreadInfo(GetWindowThreadProcessId(window.Handle, out _), ref info);
        var dom = await browser.CoreWebView2.ExecuteScriptAsync("({focused:document.hasFocus(),editor:document.activeElement.id,caret:document.querySelector('#editor').selectionStart})");
        return JsonSerializer.Serialize(new { foreground = WindowActivation.IsForeground(window),
            nativeChildFocus = valid && info.Focus != 0 && IsChild(window.Handle, info.Focus), dom = JsonSerializer.Deserialize<JsonElement>(dom) });
    });
    internal async Task<bool> HasEditorFocusAsync()
    {
        using var state = JsonDocument.Parse(await StateAsync());
        var root = state.RootElement; var dom = root.GetProperty("dom");
        return root.GetProperty("foreground").GetBoolean() && root.GetProperty("nativeChildFocus").GetBoolean()
            && dom.GetProperty("focused").GetBoolean() && dom.GetProperty("editor").GetString() == "editor" && dom.GetProperty("caret").GetInt32() == 3;
    }
    public void Dispose() { if (!window.IsDisposed) window.BeginInvoke(window.Close); thread.Join(TimeSpan.FromSeconds(10)); }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
}
