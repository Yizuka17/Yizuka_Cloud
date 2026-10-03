using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.ApplicationModel;

internal sealed class CloudTray : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly WebDavSource server;
    private readonly CloudMirror mirror;
    private readonly string root;
    private readonly Action stopClient;
    private readonly KeyboardProc keyboardCallback;
    private readonly IntPtr keyboardHook;
    private readonly ToolStripMenuItem startupItem;
    private bool refreshing;
    private bool startupStateLogged;

    private CloudTray(WebDavSource server, CloudMirror mirror, string root, Action stopClient)
    {
        this.server = server;
        this.mirror = mirror;
        this.root = root;
        this.stopClient = stopClient;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "YizukaCloud.ico");
        icon = new NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            Text = "Yizuka 云盘",
            Visible = true
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开网页端", null, async (_, _) => await OpenWebAsync());
        menu.Items.Add("打开云文件夹", null, (_, _) => Open(root));
        menu.Items.Add("立即同步", null, async (_, _) => await RefreshAsync());
        startupItem = new ToolStripMenuItem("开机自启") { CheckOnClick = false };
        startupItem.Click += async (_, _) => await ToggleStartupAsync();
        menu.Items.Add(startupItem);
        menu.Opening += async (_, _) => await UpdateStartupMenuAsync();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出客户端", null, (_, _) => ExitThread());
        icon.ContextMenuStrip = menu;
        _ = UpdateStartupMenuAsync();
        icon.DoubleClick += async (_, _) => await OpenWebAsync();
        keyboardCallback = OnKeyboard;
        using var process = Process.GetCurrentProcess();
        keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(process.MainModule!.ModuleName), 0);
    }

    public static Thread Start(WebDavSource server, CloudMirror mirror, string root, Action stopClient)
    {
        var thread = new Thread(() => Application.Run(new CloudTray(server, mirror, root, stopClient)))
        {
            Name = "Yizuka Cloud tray",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private async Task UpdateStartupMenuAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync("YizukaCloudStartup");
            if (!startupStateLogged)
            {
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName), $"{DateTime.Now:O} Windows startup task: {task.State}{Environment.NewLine}");
                startupStateLogged = true;
            }
            startupItem.Checked = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            startupItem.Enabled = task.State is StartupTaskState.Enabled or StartupTaskState.Disabled;
            startupItem.Text = task.State switch
            {
                StartupTaskState.DisabledByUser => "开机自启（请在任务管理器启用）",
                StartupTaskState.DisabledByPolicy => "开机自启（受系统策略限制）",
                StartupTaskState.EnabledByPolicy => "开机自启（受系统策略管理）",
                _ => "开机自启"
            };
        }
        catch (Exception error)
        {
            startupItem.Enabled = false;
            startupItem.Text = "开机自启（状态不可用）";
            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName), $"{DateTime.Now:O} startup state failed: {error.Message}{Environment.NewLine}");
        }
    }

    private async Task ToggleStartupAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync("YizukaCloudStartup");
            if (task.State == StartupTaskState.Enabled) task.Disable();
            else if (task.State == StartupTaskState.Disabled) await task.RequestEnableAsync();
            await UpdateStartupMenuAsync();
        }
        catch (Exception error)
        {
            icon.ShowBalloonTip(5000, "开机自启设置失败", error.Message, ToolTipIcon.Warning);
        }
    }

    private async Task OpenWebAsync()
    {
        try { Open((await server.GetWebBaseUriAsync()).AbsoluteUri); }
        catch { Open("https://cloud.17yizuka.com/"); }
    }

    private async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        icon.Text = "Yizuka 云盘：正在同步";
        try
        {
            await mirror.SyncLocalChangesAsync();
            await mirror.RefreshNewRemoteFilesAsync();
            icon.Text = "Yizuka 云盘：已同步";
        }
        catch (Exception error)
        {
            icon.Text = "Yizuka 云盘：同步失败";
            icon.ShowBalloonTip(5000, "同步失败", error.Message, ToolTipIcon.Warning);
        }
        finally { refreshing = false; }
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == (IntPtr)0x100 && Marshal.ReadInt32(data) == 0x74)
        {
            var foreground = GetForegroundWindow();
            if (IsCloudExplorerWindow(foreground))
            {
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName), $"{DateTime.Now:O} Explorer F5 requested refresh{Environment.NewLine}");
                _ = RefreshAsync();
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }

    private bool IsCloudExplorerWindow(IntPtr foreground)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application")!;
            var shell = Activator.CreateInstance(shellType)!;
            var windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            foreach (object window in (System.Collections.IEnumerable)windows!)
            {
                var handle = Convert.ToInt64(window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null));
                if (handle != foreground.ToInt64()) continue;
                var url = Convert.ToString(window.GetType().InvokeMember("LocationURL", BindingFlags.GetProperty, null, window, null));
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
                var folder = Path.GetFullPath(uri.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
                var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                return folder.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                       folder.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception error) { Console.Error.WriteLine($"Explorer F5 check failed: {error.Message}"); }
        return false;
    }

    protected override void ExitThreadCore()
    {
        if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        icon.Visible = false;
        icon.Dispose();
        stopClient();
        base.ExitThreadCore();
    }

    private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, KeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string name);
}
