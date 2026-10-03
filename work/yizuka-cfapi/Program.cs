using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.IO.Pipes;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

if (args.Length == 0 || args is ["serve-test"])
{
    using var singleton = new EventWaitHandle(false, EventResetMode.ManualReset, CloudClientConfig.SingletonName, out var createdNew);
    if (!createdNew) return;
    var rootPath = CloudClientConfig.Root;
    var serviceLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName);
    Directory.CreateDirectory(Path.GetDirectoryName(serviceLog)!);
    void LogStage(string message) => File.AppendAllText(serviceLog, $"{DateTime.Now:O} {message}{Environment.NewLine}");
    LogStage("starting");
    if (args.Length == 0 && IsPackaged())
    {
        Directory.CreateDirectory(rootPath);
    }
    using var server = WebDavSource.FromInstalledClient(CloudClientConfig.RemoteRoot);
    using var mirror = new CloudMirror(rootPath, server, unregisterOnDispose: false, registerRoot: true);
    using var stopRequested = new CancellationTokenSource();
    await mirror.StartAsync(allowOffline: true);
    LogStage("mirror connected");
    if (args.Length == 0 && IsPackaged())
    {
        await ShellRegistration.RegisterAsync(rootPath);
        LogStage("Windows Shell extension registered");
        Console.WriteLine("Packaged Windows Shell cloud-file extension registered.");
    }
    if (mirror.InitialOnline)
    {
        await mirror.SyncLocalChangesAsync();
        LogStage("initial sync complete");
    }
    else LogStage("offline: waiting to retry");
    mirror.StartWatching();
    if (args.Length == 0 && IsPackaged()) CloudTray.Start(server, mirror, rootPath, stopRequested.Cancel);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(CloudClientConfig.ControlPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync();
                LogStage($"control command: {command}");
                if (command == "refresh")
                {
                    await mirror.SyncLocalChangesAsync();
                    LogStage("manual local sync complete");
                    await mirror.RefreshNewRemoteFilesAsync();
                    LogStage("manual remote refresh complete");
                    await writer.WriteLineAsync("OK");
                }
                else if (command == "ping") await writer.WriteLineAsync("OK");
                else if (command == "shutdown")
                {
                    await writer.WriteLineAsync("OK");
                    stopRequested.Cancel();
                }
                else await writer.WriteLineAsync("ERROR: unknown command");
            }
            catch (Exception error) { LogStage($"refresh control failed: {error.Message}"); }
        }
    });
    Console.WriteLine($"Yizuka Cloud Files client active: {rootPath}");
    var refreshInterval = mirror.InitialOnline ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);
    while (!stopRequested.IsCancellationRequested)
    {
        try { await Task.Delay(refreshInterval, stopRequested.Token); }
        catch (OperationCanceledException) { break; }
        try
        {
            await mirror.SyncLocalChangesAsync();
            await mirror.RefreshNewRemoteFilesAsync();
            refreshInterval = TimeSpan.FromHours(1);
        }
        catch (Exception error)
        {
            refreshInterval = TimeSpan.FromMinutes(1);
            LogStage($"refresh failed, retrying in one minute: {error.Message}");
        }
    }
    return;
}
if (args is ["refresh-now"])
{
    using var pipe = new NamedPipeClientStream(".", CloudClientConfig.ControlPipe, PipeDirection.InOut);
    pipe.Connect(5000);
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync("refresh");
    using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var result = await reader.ReadLineAsync(responseTimeout.Token);
    if (result != "OK") throw new IOException(result ?? "Cloud refresh did not reply.");
    Console.WriteLine("PASS: cloud files refreshed immediately.");
    return;
}
if (args is ["root-safety-test"])
{
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var cloudRoot = Path.Combine(profile, "Yizuka Cloud Files");
    var junction = Path.Combine(profile, "Yizuka Cloud");
    if (CloudRootSafety.UnsafeReparseAncestor(cloudRoot) is not null)
        throw new Exception("The CFAPI root was rejected after registration.");
    if (Directory.Exists(junction) && CloudRootSafety.UnsafeReparseAncestor(junction) is null)
        throw new Exception("A server-data junction was accepted.");
    Console.WriteLine("PASS: registered cloud root is reusable; server-data junction is rejected.");
    return;
}
if (args is ["release-smoke"])
{
    var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "cloudfiles.ini");
    var releaseRoot = File.ReadAllLines(config).First(line => line.StartsWith("root=", StringComparison.OrdinalIgnoreCase))[5..];
    CloudClientConfig.EnsureSafeRoot(releaseRoot);
    using var source = WebDavSource.FromInstalledClient();
    var folder = $"发行验证-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await source.CreateDirectoryAsync(folder);
    await source.PutAsync(folder + "/远端文件.txt", Encoding.UTF8.GetBytes("release remote read"));
    await source.PutAsync(folder + "/图片.png", await File.ReadAllBytesAsync(Path.Combine("work", "yizuka-client", "thumbnail-Z.png")));
    using (var pipe = new NamedPipeClientStream(".", "YizukaCloud.CfApi.Release.Control", PipeDirection.InOut))
    {
        pipe.Connect(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("refresh");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (await reader.ReadLineAsync(timeout.Token) != "OK") throw new IOException("Release client did not refresh.");
    }
    var local = Path.Combine(releaseRoot, folder);
    var remoteFile = Path.Combine(local, "远端文件.txt");
    var photo = Path.Combine(local, "图片.png");
    var projectionDeadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < projectionDeadline && (!File.Exists(remoteFile) || !File.Exists(photo)))
    {
        if (Directory.Exists(local)) _ = Directory.GetFileSystemEntries(local);
        await Task.Delay(200);
    }
    if (!File.Exists(remoteFile) || !File.Exists(photo))
        throw new IOException("Remote placeholders were not projected.");
    var probe = Path.GetFullPath(Path.Combine("work", "yizuka-client", "ThumbnailProbe.exe"));
    var preview = Path.GetFullPath(Path.Combine("work", "yizuka-cfapi", "release-smoke-thumbnail.png"));
    using (var process = Process.Start(new ProcessStartInfo(probe)
    {
        ArgumentList = { photo, preview, "256", "8" },
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    }) ?? throw new IOException("Could not launch thumbnail probe."))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || !File.Exists(preview))
            throw new IOException($"Thumbnail probe failed: {await process.StandardOutput.ReadToEndAsync()} {await process.StandardError.ReadToEndAsync()}");
    }
    if (await File.ReadAllTextAsync(remoteFile) != "release remote read")
        throw new IOException("On-demand hydration returned the wrong data.");
    var localFile = Path.Combine(local, "本地上传.txt");
    await File.WriteAllTextAsync(localFile, "release local write");
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < deadline && !(await source.ListAsync(folder)).Any(entry => entry.RelativePath == folder + "/本地上传.txt"))
        await Task.Delay(400);
    var uploaded = await source.ReadRangeAsync(folder + "/本地上传.txt", 0, "release local write".Length);
    if (Encoding.UTF8.GetString(uploaded) != "release local write")
        throw new IOException("Local file did not upload from the release client.");
    Directory.Delete(local, recursive: true);
    deadline = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < deadline && (await source.ListAsync("")).Any(entry => entry.RelativePath == folder))
        await Task.Delay(400);
    if ((await source.ListAsync("")).Any(entry => entry.RelativePath == folder))
        throw new IOException("Release client did not delete its temporary verification folder.");
    Console.WriteLine("PASS: release root projection, on-demand read, server thumbnail, local upload and isolated cleanup.");
    return;
}
if (args is ["release-f5-create"])
{
    using var source = WebDavSource.FromInstalledClient();
    var name = $"F5验证-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    await source.PutAsync(name, Encoding.UTF8.GetBytes("F5 refresh verification"));
    Console.WriteLine(name);
    return;
}
if (args is ["remote-add-test"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    var name = $"cfapi-manual-refresh-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    await source.PutAsync(name, Encoding.UTF8.GetBytes("Created remotely to test manual refresh."));
    Console.WriteLine(name);
    return;
}
if (args is ["installed-folder-rename-test"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    var folder = $"安装态目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var renamed = folder + "-改名";
    await source.CreateDirectoryAsync(folder);
    await source.PutAsync(folder + "/离线文件.txt", Encoding.UTF8.GetBytes("installed client folder rename"));
    using (var pipe = new NamedPipeClientStream(".", "YizukaCloud.CfApiTest4.Control", PipeDirection.InOut))
    {
        pipe.Connect(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("refresh");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (await reader.ReadLineAsync(timeout.Token) != "OK") throw new IOException("Installed client did not refresh.");
    }
    var localRoot = CloudClientConfig.Root;
    var oldLocal = Path.Combine(localRoot, folder);
    var newLocal = Path.Combine(localRoot, renamed);
    var childPath = Path.Combine(oldLocal, "离线文件.txt");
    var projectedDeadline = DateTime.UtcNow.AddSeconds(6);
    while (DateTime.UtcNow < projectedDeadline && !File.Exists(childPath))
        await Task.Delay(200);
    if (!File.Exists(childPath)) throw new IOException("Installed client did not project the child file.");
    Directory.Move(oldLocal, newLocal);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline && !(await source.ListAsync("")).Any(x => x.RelativePath == renamed))
        await Task.Delay(350);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == folder) ||
        !(await source.ListAsync("")).Any(x => x.RelativePath == renamed))
        throw new IOException("Installed client's directory rename failed.");
    if (await File.ReadAllTextAsync(Path.Combine(newLocal, "离线文件.txt")) != "installed client folder rename")
        throw new IOException("Installed client's renamed child did not hydrate correctly.");
    Console.WriteLine("PASS: installed MSIX watcher moved the remote folder and opened the child from its new path.");
    return;
}
if (args is ["installed-folder-check", var folderToCheck])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    foreach (var entry in await source.ListAsync(""))
        if (entry.IsDirectory && entry.RelativePath.StartsWith(folderToCheck, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(entry.RelativePath);
    return;
}
if (args is ["installed-folder-delete-test"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    var folder = $"安装态删除目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await source.CreateDirectoryAsync(folder);
    await source.PutAsync(folder + "/文件.txt", Encoding.UTF8.GetBytes("installed folder deletion"));
    using (var pipe = new NamedPipeClientStream(".", "YizukaCloud.CfApiTest4.Control", PipeDirection.InOut))
    {
        pipe.Connect(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("refresh");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (await reader.ReadLineAsync(timeout.Token) != "OK") throw new IOException("Installed client did not refresh.");
    }
    var local = Path.Combine(CloudClientConfig.Root, folder);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(local, "文件.txt"))) await Task.Delay(250);
    if (!File.Exists(Path.Combine(local, "文件.txt"))) throw new IOException("Installed directory was not projected.");
    Directory.Delete(local, recursive: true);
    deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline && (await source.ListAsync("")).Any(x => x.RelativePath == folder)) await Task.Delay(350);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == folder)) throw new IOException("Installed watcher did not delete the remote test directory.");
    Console.WriteLine("PASS: installed MSIX watcher deleted the test folder and remote child.");
    return;
}

static bool IsPackaged()
{
    try { return !string.IsNullOrEmpty(Windows.ApplicationModel.Package.Current.Id.FullName); }
    catch (InvalidOperationException) { return false; }
}

if (args is ["shell-info"])
{
    var info = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForId(ShellRegistration.Id);
    Console.WriteLine($"Sync root: {info.Path.Path}");
    Console.WriteLine($"Icon: {info.IconResource}");
    Console.WriteLine($"Version: {info.Version}");
    return;
}

if (args is ["probe"])
{
    using var source = WebDavSource.FromInstalledClient();
    var entries = await source.ListAsync("");
    Console.WriteLine($"Authenticated WebDAV root listing: {entries.Count} entries ({entries.Count(x => x.IsDirectory)} directories, {entries.Count(x => !x.IsDirectory)} files).");
    return;
}
if (args is ["probe-tree"])
{
    using var source = WebDavSource.FromInstalledClient();
    var directories = new Stack<string>();
    directories.Push("");
    var folderCount = 0;
    var fileCount = 0;
    while (directories.TryPop(out var directory))
    {
        Console.WriteLine($"Listing: {directory}");
        var entries = await source.ListAsync(directory);
        folderCount++;
        foreach (var entry in entries)
            if (entry.IsDirectory) directories.Push(entry.RelativePath);
            else fileCount++;
    }
    Console.WriteLine($"Remote tree: {folderCount} folders, {fileCount} files.");
    return;
}
if (args is ["offline-start-test"])
{
    var offlineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "CfApiOfflineStart-" + Guid.NewGuid().ToString("N"));
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试", new Uri("http://127.0.0.1:59999/"));
    using var mirror = new CloudMirror(offlineRoot, source);
    await mirror.StartAsync(allowOffline: true);
    if (mirror.InitialOnline || !Directory.Exists(offlineRoot)) throw new IOException("Offline sync root did not stay available.");
    mirror.Dispose();
    Directory.Delete(offlineRoot);
    Console.WriteLine("PASS: offline startup keeps a cloud sync root connected for later retry.");
    return;
}
if (args is ["route-test"])
{
    using var lan = WebDavSource.FromInstalledClient(skipLocal: true);
    if ((await lan.ListAsync("")).Count == 0) throw new IOException("LAN route returned no files.");
    using var publicOnly = WebDavSource.FromInstalledClient(skipLocal: true, skipLan: true);
    if ((await publicOnly.ListAsync("")).Count == 0) throw new IOException("Cloudflare route returned no files.");
    Console.WriteLine("PASS: automatic routing reached authenticated LAN and Cloudflare endpoints.");
    return;
}
if (args is ["probe-etag"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    var entries = await source.ListAsync("");
    Console.WriteLine($"WebDAV ETags: {entries.Count(x => !x.IsDirectory && x.ETag is not null)} of {entries.Count(x => !x.IsDirectory)} files; LastModified: {entries.Count(x => !x.IsDirectory && x.ModifiedUtc is not null)}.");
    return;
}
if (args is ["probe-json-list"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    var sample = await source.InspectJsonListingAsync();
    Console.WriteLine($"Copyparty JSON listing keys: {string.Join(",", sample.Keys)}; sample timestamp: {sample.Timestamp}");
    return;
}
if (args is ["shell-unregister"])
{
    ShellRegistration.Unregister();
    Console.WriteLine("Test Shell sync root unregistered.");
    return;
}
if (args is ["shell-register"])
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Yizuka Cloud Test");
    Directory.CreateDirectory(path);
    await ShellRegistration.RegisterAsync(path);
    Console.WriteLine("Test Shell sync root registered.");
    return;
}
if (args is ["thumbnail-probe"])
{
    using var source = WebDavSource.FromInstalledClient();
    var firstLevel = await source.ListAsync("");
    RemoteEntry? photo = firstLevel.FirstOrDefault(x => !x.IsDirectory && x.RelativePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
    foreach (var folder in firstLevel.Where(x => x.IsDirectory))
    {
        if (photo is not null) break;
        var children = await source.ListAsync(folder.RelativePath);
        photo = children.FirstOrDefault(x => !x.IsDirectory && (x.RelativePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
    }
    if (photo is null) throw new InvalidOperationException("No testable image found in the first two levels.");
    var (contentType, bytes) = await source.GetThumbnailAsync(photo.RelativePath);
    Console.WriteLine($"Production thumbnail response: {contentType}, {bytes.Length} bytes.");
    return;
}
if (args is ["thumbnail-video-probe"])
{
    using var source = WebDavSource.FromInstalledClient();
    var firstLevel = await source.ListAsync("");
    RemoteEntry? video = firstLevel.FirstOrDefault(x => !x.IsDirectory && x.RelativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
    foreach (var folder in firstLevel.Where(x => x.IsDirectory))
    {
        if (video is not null) break;
        video = (await source.ListAsync(folder.RelativePath)).FirstOrDefault(x => !x.IsDirectory && x.RelativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
    }
    if (video is null) throw new InvalidOperationException("No testable video found in the first two levels.");
    var (contentType, bytes) = await source.GetThumbnailAsync(video.RelativePath);
    Console.WriteLine($"Production video thumbnail response: {contentType}, {bytes.Length} bytes.");
    return;
}
if (args is ["cfapi-video-upload"])
{
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试");
    await source.PutFileAsync("cfapi-video-preview.mp4", Path.Combine("work", "yizuka-cfapi", "cfapi-video-preview.mp4"));
    var thumbnail = await source.GetThumbnailAsync("cfapi-video-preview.mp4");
    if (thumbnail.ContentType != "image/jpeg" || thumbnail.Data.Length < 100)
        throw new IOException("Video thumbnail was not JPEG.");
    Console.WriteLine($"PASS: video uploaded; JPEG thumbnail {thumbnail.Data.Length} bytes.");
    return;
}
if (args is ["thumbnail-route-test"])
{
    foreach (var url in new[] { "https://17yizuka:8443/", "https://cloud.17yizuka.com/" })
    {
        using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试", new Uri(url));
        var thumbnail = await source.GetThumbnailAsync("cfapi-video-preview.mp4");
        if (thumbnail.ContentType != "image/jpeg" || thumbnail.Data.Length < 100)
            throw new IOException($"Invalid video thumbnail from {url}");
        Console.WriteLine($"PASS: {url} video JPEG thumbnail ({thumbnail.Data.Length} bytes).");
    }
    return;
}
if (args is ["webdav-test"])
{
    var testFolder = "CFAPI端到端测试";
    using var server = WebDavSource.FromInstalledClient();
    await server.CreateDirectoryAsync(testFolder);
    using var source = WebDavSource.FromInstalledClient(testFolder);
    var name = $"cfapi-roundtrip-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var body = Encoding.UTF8.GetBytes($"Cloud Files API WebDAV roundtrip {Guid.NewGuid():N}\r\n");
    await source.PutAsync(name, body);
    var listed = await source.ListAsync("");
    if (!listed.Any(x => x.RelativePath == name && !x.IsDirectory && x.Length == body.Length))
        throw new InvalidOperationException("The uploaded test file was not returned by PROPFIND.");
    var loaded = await source.ReadRangeAsync(name, 0, body.Length);
    if (!loaded.SequenceEqual(body)) throw new InvalidOperationException("The downloaded bytes do not match the upload.");
    var moved = name.Replace("roundtrip", "moved");
    await source.MoveAsync(name, moved);
    if (!(await source.ListAsync("")).Any(x => x.RelativePath == moved)) throw new InvalidOperationException("MOVE was not reflected in listing.");
    await source.DeleteAsync(moved);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == moved)) throw new InvalidOperationException("DELETE was not reflected in listing.");
    Console.WriteLine("PASS: WebDAV MKCOL, PUT, PROPFIND, range GET, MOVE and DELETE roundtrip in dedicated test folder.");
    return;
}
if (args is ["up2k-test-small"] or ["up2k-test-public"])
{
    var isPublic = args[0] == "up2k-test-public";
    var endpoint = new Uri(isPublic ? "https://cloud.17yizuka.com/" : "http://127.0.0.1:3924/");
    using var rootSource = WebDavSource.FromInstalledClient(endpoint: endpoint);
    await rootSource.CreateDirectoryAsync("CFAPI端到端测试");
    using var source = WebDavSource.FromInstalledClient("CFAPI端到端测试", endpoint);
    var name = $"up2k-{(isPublic ? "public" : "local")}-{DateTime.UtcNow:yyyyMMddHHmmss}.bin";
    var local = Path.Combine("work", "yizuka-cfapi", name);
    var size = isPublic ? 105L * 1024 * 1024 + 37 : 3L * 1024 * 1024 + 37;
    await using (var output = File.Create(local))
    {
        var block = new byte[1024 * 1024];
        for (long written = 0; written < size; written += block.Length)
        {
            Random.Shared.NextBytes(block);
            await output.WriteAsync(block.AsMemory(0, (int)Math.Min(block.Length, size - written)));
        }
    }
    await source.PutFileAsync(name, local, forceChunked: true);
    var remote = (await source.ListAsync("")).Single(x => x.RelativePath == name);
    if (remote.Length != size) throw new IOException($"up2k size mismatch: {remote.Length} != {size}");
    await using var input = File.OpenRead(local);
    foreach (var offset in new[] { 0L, size / 2, size - 4096 })
    {
        var expected = new byte[4096];
        input.Position = offset;
        await input.ReadExactlyAsync(expected);
        var actual = await source.ReadRangeAsync(name, offset, expected.Length);
        if (!actual.SequenceEqual(expected)) throw new IOException($"up2k byte mismatch at {offset}.");
    }
    Console.WriteLine($"PASS: up2k {(isPublic ? "Cloudflare" : "local")} upload {size} bytes, remote size and three ranges verified.");
    return;
}
if (args is ["cfapi-test"] or ["cfapi-test-no-shell"])
{
    const string testFolder = "CFAPI端到端测试";
    using var serverRoot = WebDavSource.FromInstalledClient();
    await serverRoot.CreateDirectoryAsync(testFolder);
    using var source = WebDavSource.FromInstalledClient(testFolder);
    var name = $"cfapi-content-{DateTime.UtcNow:yyyyMMddHHmmss}.bin";
    var content = new byte[8 * 1024 * 1024];
    Random.Shared.NextBytes(content);
    await source.PutAsync(name, content);
    var previewBytes = await File.ReadAllBytesAsync(Path.Combine("work", "yizuka-client", "thumbnail-Z.png"));
    await source.PutAsync("cfapi-preview-sample.png", previewBytes);
    await source.CreateDirectoryAsync("子目录");
    await source.PutAsync("子目录/嵌套测试.txt", Encoding.UTF8.GetBytes("nested CFAPI content"));
    await source.PutAsync("子目录/离线文件.txt", Encoding.UTF8.GetBytes("offline file after folder rename"));
    var mirrorRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", $"CfApiCloudTest-{DateTime.UtcNow:yyyyMMddHHmmss}");
    using var mirror = new CloudMirror(mirrorRoot, source, unregisterOnDispose: false);
    await mirror.StartAsync();
    var registerShell = args[0] == "cfapi-test";
    if (registerShell) await ShellRegistration.RegisterAsync(mirrorRoot);
    try
    {
    var probeResults = new List<long>();
    if (registerShell)
    {
        var probeExe = Path.GetFullPath(Path.Combine("work", "yizuka-client", "ThumbnailProbe.exe"));
        var previewPath = Path.Combine(mirrorRoot, "cfapi-preview-sample.png");
        await File.ReadAllBytesAsync(previewPath);
        for (var run = 1; run <= 2; run++)
        {
            var output = Path.GetFullPath(Path.Combine("work", "yizuka-cfapi", $"preview-{run}.png"));
            using var process = Process.Start(new ProcessStartInfo(probeExe)
            {
                ArgumentList = { previewPath, output, "256", "8" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not start Windows thumbnail probe.");
            var elapsed = Stopwatch.StartNew();
            await process.WaitForExitAsync();
            elapsed.Stop();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"Windows thumbnail probe failed on run {run}: {await process.StandardOutput.ReadToEndAsync()} {await process.StandardError.ReadToEndAsync()}");
            probeResults.Add(elapsed.ElapsedMilliseconds);
        }
    }
    var localPath = Path.Combine(mirrorRoot, name);
    var before = File.GetAttributes(localPath);
    if (!before.HasFlag(FileAttributes.ReparsePoint) || !before.HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException($"The WebDAV file is not an offline placeholder: {before}");
    await using (var partial = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        partial.Seek(64 * 1024, SeekOrigin.Begin);
        var fragment = new byte[256];
        await partial.ReadExactlyAsync(fragment);
        if (!fragment.SequenceEqual(content.AsSpan(64 * 1024, 256).ToArray()))
            throw new InvalidOperationException("Progressive partial read returned incorrect data.");
    }
    var loaded = await File.ReadAllBytesAsync(localPath);
    if (!loaded.SequenceEqual(content)) throw new InvalidOperationException("Cloud Files API hydrated bytes do not match the WebDAV source.");
    var after = File.GetAttributes(localPath);
    if (after.HasFlag(FileAttributes.Offline)) throw new InvalidOperationException("Cloud file is still offline after reading.");
    var nested = await File.ReadAllTextAsync(Path.Combine(mirrorRoot, "子目录", "嵌套测试.txt"));
    if (nested != "nested CFAPI content") throw new InvalidOperationException("Nested folder hydration failed.");
    var renamedFolder = $"重命名目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var renamedFolderPath = Path.Combine(mirrorRoot, renamedFolder);
    Directory.Move(Path.Combine(mirrorRoot, "子目录"), renamedFolderPath);
    mirror.MarkDirectoryMoveForTest(Path.Combine(mirrorRoot, "子目录"), renamedFolderPath);
    await mirror.SyncLocalChangesAsync();
    if ((await source.ListAsync("")).Any(x => x.RelativePath == "子目录") ||
        !(await source.ListAsync("")).Any(x => x.RelativePath == renamedFolder))
        throw new InvalidOperationException("Folder rename was not reflected remotely.");
    if (!File.GetAttributes(Path.Combine(renamedFolderPath, "离线文件.txt")).HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException("Folder rename unexpectedly downloaded an offline child.");
    if (await File.ReadAllTextAsync(Path.Combine(renamedFolderPath, "离线文件.txt")) != "offline file after folder rename")
        throw new InvalidOperationException("Offline child did not hydrate after folder rename.");
    var remoteNew = $"cfapi-remote-new-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var remoteNewBody = Encoding.UTF8.GetBytes("added by remote website while mirror is connected");
    await source.PutAsync(remoteNew, remoteNewBody);
    await mirror.RefreshNewRemoteFilesAsync();
    var remoteNewPath = Path.Combine(mirrorRoot, remoteNew);
    if (!File.GetAttributes(remoteNewPath).HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException("New remote file was not projected as an offline placeholder.");
    if (!(await File.ReadAllBytesAsync(remoteNewPath)).SequenceEqual(remoteNewBody))
        throw new InvalidOperationException("New remote file hydration failed.");
    await Task.Delay(1100);
    var remoteUpdatedBody = Encoding.UTF8.GetBytes("remote file modified after initial hydration");
    await source.PutAsync(remoteNew, remoteUpdatedBody);
    await mirror.RefreshNewRemoteFilesAsync();
    if (!File.GetAttributes(remoteNewPath).HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException("Remote modification was not projected as a fresh placeholder.");
    if (!(await File.ReadAllBytesAsync(remoteNewPath)).SequenceEqual(remoteUpdatedBody))
        throw new InvalidOperationException("Remote modified bytes did not hydrate correctly.");
    await source.DeleteAsync(remoteNew);
    await mirror.RefreshNewRemoteFilesAsync();
    if (File.Exists(remoteNewPath))
        throw new InvalidOperationException("Remote deletion was not reflected locally.");
    var uploadName = $"cfapi-local-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var uploadPath = Path.Combine(mirrorRoot, uploadName);
    var firstVersion = Encoding.UTF8.GetBytes("created in local Cloud Files folder");
    await File.WriteAllBytesAsync(uploadPath, firstVersion);
    await mirror.SyncLocalChangesAsync();
    if (!(await source.ReadRangeAsync(uploadName, 0, firstVersion.Length)).SequenceEqual(firstVersion))
        throw new InvalidOperationException("Local creation was not uploaded correctly.");
    var secondVersion = Encoding.UTF8.GetBytes("modified in local Cloud Files folder; second version");
    await File.WriteAllBytesAsync(uploadPath, secondVersion);
    await mirror.SyncLocalChangesAsync();
    if (!(await source.ReadRangeAsync(uploadName, 0, secondVersion.Length)).SequenceEqual(secondVersion))
        throw new InvalidOperationException("Local modification was not uploaded correctly.");
    var renamed = uploadName.Replace("local", "renamed");
    File.Move(uploadPath, Path.Combine(mirrorRoot, renamed));
    mirror.MarkLocalDeletionForTest(uploadPath);
    await mirror.SyncLocalChangesAsync();
    var afterRename = await source.ListAsync("");
    if (!afterRename.Any(x => x.RelativePath == renamed) || afterRename.Any(x => x.RelativePath == uploadName))
        throw new InvalidOperationException("Local rename was not reflected remotely.");
    File.Delete(Path.Combine(mirrorRoot, renamed));
    mirror.MarkLocalDeletionForTest(Path.Combine(mirrorRoot, renamed));
    await mirror.SyncLocalChangesAsync();
    if ((await source.ListAsync("")).Any(x => x.RelativePath == renamed))
        throw new InvalidOperationException("Local deletion was not reflected remotely.");
    mirror.StartWatching();
    var watchedName = $"cfapi-watched-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var watchedBody = Encoding.UTF8.GetBytes("automatic file watcher upload");
    await File.WriteAllBytesAsync(Path.Combine(mirrorRoot, watchedName), watchedBody);
    var deadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < deadline && !(await source.ListAsync("")).Any(x => x.RelativePath == watchedName))
        await Task.Delay(250);
    if (!(await source.ListAsync("")).Any(x => x.RelativePath == watchedName))
        throw new InvalidOperationException("Automatic watcher did not upload the local file.");
    if (!(await source.ReadRangeAsync(watchedName, 0, watchedBody.Length)).SequenceEqual(watchedBody))
        throw new InvalidOperationException("Automatic watcher uploaded incorrect bytes.");
    File.Delete(Path.Combine(mirrorRoot, watchedName));
    deadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < deadline && (await source.ListAsync("")).Any(x => x.RelativePath == watchedName))
        await Task.Delay(250);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == watchedName))
        throw new InvalidOperationException("Automatic watcher did not delete the remote file.");
    var remoteDeleteFolder = $"目录删除探针-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await source.CreateDirectoryAsync(remoteDeleteFolder);
    await source.PutAsync(remoteDeleteFolder + "/文件.txt", Encoding.UTF8.GetBytes("folder delete probe"));
    await source.DeleteAsync(remoteDeleteFolder);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == remoteDeleteFolder))
        throw new InvalidOperationException("WebDAV directory DELETE was not recursive.");
    var watchedFolder = $"监听目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var watchedRenamed = watchedFolder + "-改名";
    await source.CreateDirectoryAsync(watchedFolder);
    await source.PutAsync(watchedFolder + "/文件.txt", Encoding.UTF8.GetBytes("watched folder rename"));
    await mirror.RefreshNewRemoteFilesAsync();
    Directory.Move(Path.Combine(mirrorRoot, watchedFolder), Path.Combine(mirrorRoot, watchedRenamed));
    deadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < deadline && !(await source.ListAsync("")).Any(x => x.RelativePath == watchedRenamed))
        await Task.Delay(250);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == watchedFolder) ||
        !(await source.ListAsync("")).Any(x => x.RelativePath == watchedRenamed))
        throw new InvalidOperationException("Automatic watcher did not rename the remote folder.");
    if (!File.GetAttributes(Path.Combine(mirrorRoot, watchedRenamed, "文件.txt")).HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException("Watched folder rename downloaded its offline child.");
    var watchedDeletedFolder = $"监听删除目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await source.CreateDirectoryAsync(watchedDeletedFolder);
    await source.PutAsync(watchedDeletedFolder + "/文件.txt", Encoding.UTF8.GetBytes("watched folder delete"));
    await mirror.RefreshNewRemoteFilesAsync();
    Directory.Delete(Path.Combine(mirrorRoot, watchedDeletedFolder), recursive: true);
    deadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < deadline && (await source.ListAsync("")).Any(x => x.RelativePath == watchedDeletedFolder))
        await Task.Delay(250);
    if ((await source.ListAsync("")).Any(x => x.RelativePath == watchedDeletedFolder))
        throw new InvalidOperationException("Automatic watcher did not delete the remote directory.");
    mirror.StopWatching();
    var journalFile = $"待恢复删除-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var conflictFile = $"待恢复冲突-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var journalFolder = $"待恢复目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var journalDeletedFolder = $"待恢复删目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var journalConflictFolder = $"待恢复冲突目录-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await source.PutAsync(journalFile, Encoding.UTF8.GetBytes("pending deletion"));
    await source.PutAsync(conflictFile, Encoding.UTF8.GetBytes("old remote version"));
    await source.CreateDirectoryAsync(journalFolder);
    await source.PutAsync(journalFolder + "/离线.txt", Encoding.UTF8.GetBytes("pending folder move"));
    await source.CreateDirectoryAsync(journalDeletedFolder);
    await source.PutAsync(journalDeletedFolder + "/文件.txt", Encoding.UTF8.GetBytes("pending folder deletion"));
    await source.CreateDirectoryAsync(journalConflictFolder);
    await source.PutAsync(journalConflictFolder + "/文件.txt", Encoding.UTF8.GetBytes("old folder version"));
    var journalChild = Path.Combine(mirrorRoot, journalFolder, "离线.txt");
    var journalDeadline = DateTime.UtcNow.AddSeconds(12);
    while (DateTime.UtcNow < journalDeadline && (!File.Exists(journalChild) ||
        !File.Exists(Path.Combine(mirrorRoot, journalDeletedFolder, "文件.txt")) ||
        !File.Exists(Path.Combine(mirrorRoot, journalConflictFolder, "文件.txt"))))
    {
        await mirror.RefreshNewRemoteFilesAsync();
        await Task.Delay(300);
    }
    if (!File.Exists(journalChild) ||
        !File.Exists(Path.Combine(mirrorRoot, journalDeletedFolder, "文件.txt")) ||
        !File.Exists(Path.Combine(mirrorRoot, journalConflictFolder, "文件.txt")))
        throw new InvalidOperationException("Journal test child was not projected before restart.");
    File.Delete(Path.Combine(mirrorRoot, journalFile));
    mirror.MarkLocalDeletionForTest(Path.Combine(mirrorRoot, journalFile));
    File.Delete(Path.Combine(mirrorRoot, conflictFile));
    mirror.MarkLocalDeletionForTest(Path.Combine(mirrorRoot, conflictFile));
    Directory.Delete(Path.Combine(mirrorRoot, journalDeletedFolder), recursive: true);
    mirror.MarkLocalDeletionForTest(Path.Combine(mirrorRoot, journalDeletedFolder));
    Directory.Delete(Path.Combine(mirrorRoot, journalConflictFolder), recursive: true);
    mirror.MarkLocalDeletionForTest(Path.Combine(mirrorRoot, journalConflictFolder));
    await Task.Delay(1100);
    await source.PutAsync(conflictFile, Encoding.UTF8.GetBytes("server changed after local deletion"));
    await source.PutAsync(journalConflictFolder + "/文件.txt", Encoding.UTF8.GetBytes("new folder version"));
    var movedJournalFolder = journalFolder + "-改名";
    Directory.Move(Path.Combine(mirrorRoot, journalFolder), Path.Combine(mirrorRoot, movedJournalFolder));
    mirror.MarkDirectoryMoveForTest(Path.Combine(mirrorRoot, journalFolder), Path.Combine(mirrorRoot, movedJournalFolder));
    var offlineEditFile = $"离线修改-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    var offlineConflictFile = $"离线双端修改-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
    await source.PutAsync(offlineEditFile, Encoding.UTF8.GetBytes("original local"));
    await source.PutAsync(offlineConflictFile, Encoding.UTF8.GetBytes("original conflict"));
    await mirror.RefreshNewRemoteFilesAsync();
    _ = await File.ReadAllTextAsync(Path.Combine(mirrorRoot, offlineEditFile));
    _ = await File.ReadAllTextAsync(Path.Combine(mirrorRoot, offlineConflictFile));
    mirror.Dispose();
    await File.WriteAllTextAsync(Path.Combine(mirrorRoot, offlineEditFile), "local edit while stopped");
    await File.WriteAllTextAsync(Path.Combine(mirrorRoot, offlineConflictFile), "local conflicting edit while stopped");
    await Task.Delay(1100);
    await source.PutAsync(offlineConflictFile, Encoding.UTF8.GetBytes("newer remote edit"));
    using (var resumed = new CloudMirror(mirrorRoot, source, unregisterOnDispose: false, registerRoot: false))
    {
        await resumed.StartAsync();
        await resumed.SyncLocalChangesAsync();
        if ((await source.ListAsync("")).Any(x => x.RelativePath == journalFile || x.RelativePath == journalFolder) ||
            !(await source.ListAsync("")).Any(x => x.RelativePath == movedJournalFolder))
            throw new InvalidOperationException("Pending delete or folder rename did not survive client restart.");
        if ((await source.ListAsync("")).Any(x => x.RelativePath == journalDeletedFolder) ||
            !(await source.ListAsync("")).Any(x => x.RelativePath == journalConflictFolder) ||
            await File.ReadAllTextAsync(Path.Combine(mirrorRoot, journalConflictFolder, "文件.txt")) != "new folder version")
            throw new InvalidOperationException("Restarted directory deletion failed or removed a newer folder version.");
        if (!(await source.ListAsync("")).Any(x => x.RelativePath == conflictFile) ||
            await File.ReadAllTextAsync(Path.Combine(mirrorRoot, conflictFile)) != "server changed after local deletion")
            throw new InvalidOperationException("Restarted client deleted a newer remote version.");
        if (!File.GetAttributes(Path.Combine(mirrorRoot, movedJournalFolder, "离线.txt")).HasFlag(FileAttributes.Offline))
            throw new InvalidOperationException("Restarted directory move downloaded an offline file.");
        if (await File.ReadAllTextAsync(Path.Combine(mirrorRoot, offlineEditFile)) != "local edit while stopped" ||
            Encoding.UTF8.GetString(await source.ReadRangeAsync(offlineEditFile, 0, "local edit while stopped".Length)) != "local edit while stopped")
            throw new InvalidOperationException("Local edit while stopped did not upload after restart.");
        if (await File.ReadAllTextAsync(Path.Combine(mirrorRoot, offlineConflictFile)) != "local conflicting edit while stopped" ||
            Encoding.UTF8.GetString(await source.ReadRangeAsync(offlineConflictFile, 0, "newer remote edit".Length)) != "newer remote edit")
            throw new InvalidOperationException("Restarted client overwrote one side of a two-sided edit conflict.");
    }
    Console.WriteLine($"PASS: server PUT -> CFAPI placeholder -> range FETCH_DATA -> Explorer-compatible local file ({loaded.Length} bytes).");
    Console.WriteLine("PASS: local create, modify, rename and delete -> WebDAV write-back in test folder.");
    Console.WriteLine("PASS: directory rename updates remote path and offline child placeholder identity.");
    Console.WriteLine("PASS: remote create, modify and delete -> manual metadata refresh -> Explorer placeholders.");
    Console.WriteLine("PASS: FileSystemWatcher automatically uploads creations and removes deleted files.");
    Console.WriteLine("PASS: FileSystemWatcher renames folders without hydrating offline children.");
    Console.WriteLine("PASS: FileSystemWatcher deletes directories and their remote children.");
    Console.WriteLine("PASS: pending file delete and directory rename survive client restart.");
    Console.WriteLine("PASS: deletion after restart preserves a newer server version.");
    Console.WriteLine("PASS: directory deletion after restart preserves a newer server child.");
    Console.WriteLine("PASS: local edits while stopped upload on restart; two-sided edits preserve both versions.");
    if (registerShell) Console.WriteLine("PASS: Windows Shell sync root registration for Explorer.");
    if (registerShell) Console.WriteLine($"Windows thumbnail probe: first {probeResults[0]} ms, repeated {probeResults[1]} ms.");
    Console.WriteLine($"Test folder: {mirrorRoot}");
    }
    finally
    {
        mirror.Dispose();
        CfUnregisterSyncRoot(mirrorRoot);
        if (registerShell) ShellRegistration.Unregister();
    }
    return;
}

// Isolated Cloud Files API proof of concept. Never points at the production Y: or Z: drives.
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "CfApiPilot");
Directory.CreateDirectory(root);
var fileName = $"cfapi-pilot-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
var contents = Encoding.UTF8.GetBytes("Yizuka Cloud CFAPI on-demand hydration pilot.\r\n");
var filePath = Path.Combine(root, fileName);
if (File.Exists(filePath))
{
    Console.WriteLine($"Pilot file already exists: {filePath}");
    return;
}

var registration = new CF_SYNC_REGISTRATION
{
    StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
    ProviderName = "Yizuka Cloud Pilot",
    ProviderVersion = "0.1.0",
    ProviderId = new Guid("7aac58ea-16ac-4e62-9c84-2b0d21d1ec5b")
};
var policies = new CF_SYNC_POLICIES
{
    StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
    Hydration = new CF_HYDRATION_POLICY { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL },
    Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL },
    InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME,
    HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE
};

var registered = false;
var connected = false;
CF_CONNECTION_KEY connectionKey = default;
CF_CALLBACK fetch = (in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters) =>
{
    try
    {
        var request = parameters.GetParam<CF_CALLBACK_PARAMETERS.FETCHDATA>();
        Console.WriteLine($"FETCH_DATA offset={request.RequiredFileOffset} length={request.RequiredLength}");
        var offset = checked((int)request.RequiredFileOffset);
        var length = checked((int)Math.Min(request.RequiredLength, contents.Length - offset));
        if (length < 0) throw new InvalidOperationException("Invalid fetch range.");
        var buffer = Marshal.AllocHGlobal(Math.Max(length, 1));
        try
        {
            if (length > 0) Marshal.Copy(contents, offset, buffer, length);
            var operation = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = info.ConnectionKey,
                TransferKey = info.TransferKey,
                RequestKey = info.RequestKey
            };
            var opParameters = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Buffer = buffer,
                Offset = request.RequiredFileOffset,
                Length = length
            });
            CfExecute(operation, ref opParameters).ThrowIfFailed();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    catch (Exception ex) { Console.Error.WriteLine($"FETCH_DATA failed: {ex}"); }
};
var callbacks = new[]
{
    new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = fetch },
    CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
};

try
{
    CfRegisterSyncRoot(root, in registration, in policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE).ThrowIfFailed();
    registered = true;
    CfConnectSyncRoot(root, callbacks, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out connectionKey).ThrowIfFailed();
    connected = true;

    var now = DateTime.UtcNow.ToFileTimeUtc();
    var fileTime = new System.Runtime.InteropServices.ComTypes.FILETIME
    {
        dwLowDateTime = unchecked((int)now),
        dwHighDateTime = unchecked((int)(now >> 32))
    };
    var identity = Marshal.StringToHGlobalUni(fileName);
    try
    {
    var placeholder = new CF_PLACEHOLDER_CREATE_INFO
    {
        RelativeFileName = fileName,
        FileIdentity = identity,
        FileIdentityLength = (uint)((fileName.Length + 1) * 2),
        FsMetadata = new CF_FS_METADATA
        {
            FileSize = contents.Length,
            BasicInfo = new Vanara.PInvoke.Kernel32.FILE_BASIC_INFO
            {
                FileAttributes = FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                CreationTime = fileTime,
                LastWriteTime = fileTime,
                LastAccessTime = fileTime,
                ChangeTime = fileTime
            }
        },
        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
    };
    var entries = new[] { placeholder };
    CfCreatePlaceholders(root, entries, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var processed).ThrowIfFailed();
    entries[0].Result.ThrowIfFailed();
    Console.WriteLine($"Created {processed} placeholder: {filePath}");
    var beforeRead = File.GetAttributes(filePath);
    if (!beforeRead.HasFlag(FileAttributes.ReparsePoint) || !beforeRead.HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException($"The created file is not an offline cloud placeholder: {beforeRead}");
    Console.WriteLine($"Before read: {new FileInfo(filePath).Length} logical bytes, attributes={beforeRead}");
    var actual = File.ReadAllBytes(filePath);
    if (!actual.SequenceEqual(contents)) throw new InvalidOperationException("Hydrated content does not match source.");
    var afterRead = File.GetAttributes(filePath);
    if (afterRead.HasFlag(FileAttributes.Offline))
        throw new InvalidOperationException($"The placeholder is still offline after hydration: {afterRead}");
    Console.WriteLine("PASS: opening the placeholder fetched the expected content on demand.");
    Console.WriteLine($"Pilot sync root: {root}");
    }
    finally { Marshal.FreeHGlobal(identity); }
}
finally
{
    if (connected) CfDisconnectSyncRoot(connectionKey);
    if (registered) CfUnregisterSyncRoot(root);
}
