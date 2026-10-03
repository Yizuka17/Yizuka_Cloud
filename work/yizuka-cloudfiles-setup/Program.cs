using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
if (args is ["--install-default"])
{
    try { await SetupForm.InstallDefaultAsync(); }
    catch (Exception error)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), SetupForm.LogName), error.ToString());
        Environment.ExitCode = 1;
    }
    return;
}
Application.Run(new SetupForm());

internal sealed class SetupForm : Form
{
#if YIZUKA_RELEASE
    internal const string LogName = "YizukaCloudSetup.log";
    private const string ConfigName = "cloudfiles.ini";
    private const string PackageName = "Yizuka.CloudFiles.msix";
    private const string PackageFamily = "Yizuka.CloudFiles_r2dfm4c685mge!App";
    private const string ControlPipe = "YizukaCloud.CfApi.Release.Control";
    private const string DefaultFolder = "Yizuka Cloud Files";
#else
    internal const string LogName = "YizukaCloudFilesSetup-Test.log";
    private const string ConfigName = "cloudfiles-test.ini";
    private const string PackageName = "Yizuka.CloudFiles.Test.msix";
    private const string PackageFamily = "Yizuka.CloudFiles.Test_r2dfm4c685mge!App";
    private const string ControlPipe = "YizukaCloud.CfApiTest4.Control";
    private const string DefaultFolder = "Yizuka Cloud Test";
#endif
    private readonly TextBox rootBox = new() { Width = 365 };
    private readonly TextBox userBox = new() { Width = 365 };
    private readonly TextBox passwordBox = new() { Width = 365, UseSystemPasswordChar = true };
    private readonly Button installButton = new() { Text = "安装并启动云盘", Width = 190, Height = 36 };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud");

    public SetupForm()
    {
        Text = "Yizuka 云盘安装程序";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(510, 345);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 8 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        var title = new Label { Text = "Yizuka 云文件", Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(title, 0, 0); panel.SetColumnSpan(title, 2);
#if YIZUKA_RELEASE
        var note = new Label { Text = "连接你的正式云盘。安装会在当前 Windows 用户账户中信任此安装包的自签名证书。", AutoSize = true, MaximumSize = new Size(450, 0) };
#else
        var note = new Label { Text = "测试版只连接“CFAPI端到端测试”目录，不替换现有 Z:。安装会信任此测试包的签名证书（仅当前用户）。", AutoSize = true, MaximumSize = new Size(450, 0) };
#endif
        panel.Controls.Add(note, 0, 1); panel.SetColumnSpan(note, 2);
        AddRow(panel, 2, "云文件夹", rootBox);
        AddRow(panel, 3, "账户", userBox);
        AddRow(panel, 4, "密码", passwordBox);
        rootBox.Text = InitialRoot();
        userBox.Text = ReadSetting("username", "admin", "settings.ini");
        passwordBox.PlaceholderText = File.Exists(Path.Combine(DataDir, "password.dpapi")) ? "留空则使用现有授权" : "请输入云盘密码";
        var hint = new Label { Text = "云文件夹同时是按需缓存位置；可选本机 NTFS / ReFS 分区。", AutoSize = true };
        panel.Controls.Add(hint, 0, 5); panel.SetColumnSpan(hint, 2);
        panel.Controls.Add(installButton, 1, 6);
        panel.Controls.Add(status, 0, 7); panel.SetColumnSpan(status, 2);
        installButton.Click += async (_, _) => await InstallAsync();
    }

    private static void AddRow(TableLayoutPanel panel, int row, string name, Control control)
    {
        panel.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static string ReadSetting(string key, string fallback, string filename)
    {
        var path = Path.Combine(DataDir, filename);
        if (!File.Exists(path)) return fallback;
        return File.ReadAllLines(path).FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..] ?? fallback;
    }

    private static string InitialRoot()
    {
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolder);
        var configured = ReadSetting("root", fallback, ConfigName);
        return CloudRootSafety.UnsafeReparseAncestor(configured) is null ? configured : fallback;
    }

    private static void SaveSetting(string key, string value, string filename)
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, filename);
        var lines = File.Exists(path) ? File.ReadAllLines(path).Where(line => !line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
        lines.Add(key + "=" + value);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string ValidateRoot(string value)
    {
        var path = Path.GetFullPath(value.Trim());
        if (!Path.IsPathFullyQualified(path) || path == Path.GetPathRoot(path)) throw new IOException("请选择本机磁盘上的专用文件夹。");
        var reparse = CloudRootSafety.UnsafeReparseAncestor(path);
        if (reparse is not null) throw new IOException("缓存位置不能经过目录联接点或其他重解析点：" + reparse);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || (drive.DriveFormat != "NTFS" && drive.DriveFormat != "ReFS"))
            throw new IOException("云文件夹必须位于本机 NTFS 或 ReFS 分区。");
        var existing = ReadSetting("root", "", ConfigName);
        if (!path.Equals(existing, StringComparison.OrdinalIgnoreCase) && Directory.Exists(existing) && CloudRootSafety.UnsafeReparseAncestor(existing) is null && Directory.EnumerateFileSystemEntries(existing).Any())
            throw new IOException("当前测试云文件夹已有文件，不能直接改换缓存位置。请先迁移后再安装。");
        if (Directory.Exists(path) && !path.Equals(existing, StringComparison.OrdinalIgnoreCase) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("请选择空文件夹，避免覆盖已有文件。");
#if YIZUKA_RELEASE
        if (path.Equals(@"Y:\files", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"Y:\files\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("缓存文件夹不能放在本机云盘服务器的数据目录中。");
#endif
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExistingPassword()
    {
        var path = Path.Combine(DataDir, "password.dpapi");
        if (!File.Exists(path)) throw new IOException("请输入云盘密码。");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
    }

    private static async Task VerifyAccountAsync(string username, string password)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "https://cloud.17yizuka.com/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.MultiStatus) throw new IOException("账号或密码验证失败，或公网云盘暂时不可用。");
    }

    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new IOException("安装包缺少 " + name);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private async Task InstallAsync()
    {
        installButton.Enabled = false;
        try
        {
            await InstallCoreAsync(rootBox.Text, userBox.Text.Trim(), passwordBox.Text.Length > 0 ? passwordBox.Text : ExistingPassword(), message => status.Text = message);
            passwordBox.Clear();
        }
        catch (Exception error) { status.Text = error.Message; }
        finally { installButton.Enabled = true; }
    }

    internal static async Task InstallDefaultAsync()
    {
        var root = InitialRoot();
        var username = ReadSetting("username", "admin", "settings.ini");
        var log = Path.Combine(Path.GetTempPath(), LogName);
        File.WriteAllText(log, "");
        await InstallCoreAsync(root, username, ExistingPassword(), message => File.AppendAllText(log, message + Environment.NewLine));
    }

    private static async Task InstallCoreAsync(string rootChoice, string username, string password, Action<string> report)
    {
            report("验证账号与缓存位置…");
            var root = ValidateRoot(rootChoice);
            if (username.Length == 0) throw new IOException("请输入账号。");
            await VerifyAccountAsync(username, password);
            report("安装签名证书与云文件组件…");
            using (var certificate = new X509Certificate2(Resource("Yizuka.CloudFiles.Test.cer")))
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
                    store.Add(certificate);
            }
            var temp = Path.Combine(Path.GetTempPath(), "YizukaCloudSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var package = Path.Combine(temp, "cloudfiles.msix");
            await File.WriteAllBytesAsync(package, Resource(PackageName));
            var command = "Add-AppxPackage -Path '" + package.Replace("'", "''") + "' -ForceApplicationShutdown";
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }) ?? throw new IOException("无法启动 Windows 安装服务。");
            await process.WaitForExitAsync();
            var error = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0) throw new IOException("MSIX 安装失败：" + error);
            SaveSetting("username", username, "settings.ini");
            File.WriteAllBytes(Path.Combine(DataDir, "password.dpapi"), ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
            SaveSetting("root", root, ConfigName);
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { "shell:AppsFolder\\" + PackageFamily },
                UseShellExecute = true
            });
            report("正在等待云盘客户端完成首次连接…");
            await VerifyStartupAsync();
            report("安装完成。云文件夹已在资源管理器中注册。");
    }

    private static async Task VerifyStartupAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", ControlPipe, PipeDirection.InOut);
                await pipe.ConnectAsync(2000);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("ping");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await reader.ReadLineAsync(timeout.Token) == "OK") return;
            }
            catch (Exception error) { lastError = error; }
            await Task.Delay(1000);
        }
        throw new IOException("安装已完成，但客户端没有正常启动。请查看云盘客户端日志。", lastError);
    }
}
