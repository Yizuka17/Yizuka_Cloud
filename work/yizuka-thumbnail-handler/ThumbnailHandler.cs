using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Reflection;

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem item, uint mode);
}

[Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint width, out IntPtr bitmap, out uint alphaType);
}

#if YIZUKA_RELEASE
[ComVisible(true), Guid("8EE88A02-7A44-43D2-A88A-662D66142595"), ClassInterface(ClassInterfaceType.None)]
#else
[ComVisible(true), Guid("1FD5E048-9BF4-4888-BC4F-82A0836A7B5D"), ClassInterface(ClassInterfaceType.None)]
#endif
public sealed class YizukaThumbnailHandler : IInitializeWithItem, IThumbnailProvider
{
    internal static long LastActivityTicks = DateTime.UtcNow.Ticks;
    internal static int ActiveCalls;
    private string relativePath;
    private string cacheVersion;
    private static readonly string root = ReadRoot();

    private static string ReadRoot()
    {
#if YIZUKA_RELEASE
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "cloudfiles.ini");
#else
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "cloudfiles-test.ini");
#endif
        if (File.Exists(config))
            foreach (var line in File.ReadAllLines(config))
                if (line.StartsWith("root=", StringComparison.OrdinalIgnoreCase) && line.Length > 5)
                    return Path.GetFullPath(line.Substring(5).Trim());
#if YIZUKA_RELEASE
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Yizuka Cloud Files");
#else
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Yizuka Cloud Test");
#endif
    }
    private static readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud");
    private static readonly string[] endpoints = { "http://127.0.0.1:3924/", "https://17yizuka:8443/", "https://cloud.17yizuka.com/" };
    private static string currentEndpoint;

    public int Initialize(IShellItem item, uint mode)
    {
        Interlocked.Increment(ref ActiveCalls);
        Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
        IntPtr name = IntPtr.Zero;
        try
        {
            int result = item.GetDisplayName(0x80058000, out name); // SIGDN_FILESYSPATH
            if (result < 0) return result;
            string path = Marshal.PtrToStringUni(name);
            if (path == null || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return unchecked((int)0x80070057);
            relativePath = path.Substring(root.Length + 1).Replace('\\', '/');
            var file = new FileInfo(path);
            cacheVersion = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
            return 0;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
        finally
        {
            if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
            Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref ActiveCalls);
        }
    }

    public int GetThumbnail(uint width, out IntPtr bitmap, out uint alphaType)
    {
        Interlocked.Increment(ref ActiveCalls);
        Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
        bitmap = IntPtr.Zero;
        alphaType = 1; // WTSAT_RGB
        try
        {
            if (string.IsNullOrEmpty(relativePath)) return unchecked((int)0x80070057);
            byte[] image = ReadThumbnail(relativePath, cacheVersion);
            using (var source = Image.FromStream(new MemoryStream(image)))
            {
                int side = (int)Math.Min(Math.Max(width, 1), 1024);
                double scale = Math.Min(1.0, Math.Min((double)side / source.Width, (double)side / source.Height));
                int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                using (var output = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(output))
                    {
                        graphics.Clear(Color.White);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(source, 0, 0, w, h);
                    }
                    bitmap = output.GetHbitmap(Color.White);
                }
            }
            return 0;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
        finally
        {
            Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref ActiveCalls);
        }
    }

    private static byte[] ReadThumbnail(string relativePath, string version)
    {
#if YIZUKA_RELEASE
        var cacheDir = Path.Combine(dataDir, "ThumbnailCacheRelease");
#else
        var cacheDir = Path.Combine(dataDir, "ThumbnailCache");
#endif
        Directory.CreateDirectory(cacheDir);
        string key;
        using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(relativePath + "\n" + version))).Replace("-", "");
        var cacheFile = Path.Combine(cacheDir, key + ".jpg");
        if (File.Exists(cacheFile)) return File.ReadAllBytes(cacheFile);

        var settings = File.ReadAllLines(Path.Combine(dataDir, "settings.ini"));
        string user = null;
        foreach (var line in settings) if (line.StartsWith("username=", StringComparison.OrdinalIgnoreCase)) user = line.Substring(9).Trim();
        if (string.IsNullOrEmpty(user)) throw new InvalidOperationException("Cloud account is not authorized.");
        string password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "password.dpapi")), null, DataProtectionScope.CurrentUser));
        Exception lastError = null;
        var preferred = currentEndpoint;
        var order = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(preferred)) order.Add(preferred);
        foreach (var endpoint in endpoints) if (!order.Contains(endpoint)) order.Add(endpoint);
        foreach (var endpoint in order)
        {
            try
            {
#if YIZUKA_RELEASE
                var url = endpoint + string.Join("/", Array.ConvertAll(relativePath.Split('/'), Uri.EscapeDataString)) + "?th=j";
#else
                var url = endpoint + "CFAPI%E7%AB%AF%E5%88%B0%E7%AB%AF%E6%B5%8B%E8%AF%95/" + string.Join("/", Array.ConvertAll(relativePath.Split('/'), Uri.EscapeDataString)) + "?th=j";
#endif
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
                request.Timeout = endpoint == endpoints[2] ? 12000 : 2500;
                if (endpoint != endpoints[2]) request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.ContentType == null || !response.ContentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The cloud server did not return a JPEG thumbnail.");
                    using (var stream = response.GetResponseStream())
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        var bytes = memory.ToArray();
                        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Thumbnail exceeded size limit.");
                        File.WriteAllBytes(cacheFile, bytes);
                        currentEndpoint = endpoint;
                        return bytes;
                    }
                }
            }
            catch (Exception error) { lastError = error; }
        }
        throw new IOException("All cloud thumbnail endpoints failed.", lastError);
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        var registrar = new RegistrationServices();
        int cookie = registrar.RegisterTypeForComClients(typeof(YizukaThumbnailHandler), RegistrationClassContext.LocalServer, RegistrationConnectionType.MultipleUse);
        try
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                Console.WriteLine("COM thumbnail handler registered, cookie=" + cookie);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--render-test")
            {
                var handler = new YizukaThumbnailHandler();
                typeof(YizukaThumbnailHandler).GetField("relativePath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(handler, args[1]);
                IntPtr bitmap;
                uint alpha;
                int result = handler.GetThumbnail(256, out bitmap, out alpha);
                if (result < 0 || bitmap == IntPtr.Zero) throw new COMException("Thumbnail rendering failed.", result);
                try { using (var image = Bitmap.FromHbitmap(bitmap)) image.Save(args[2]); }
                finally { DeleteObject(bitmap); }
                Console.WriteLine("PASS: fetched JPEG thumbnail and returned HBITMAP");
                return 0;
            }
            var idleTimeout = args.Length > 0 && args[0] == "--idle-test" ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(2);
            while (true)
            {
                Thread.Sleep(1000);
                var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref YizukaThumbnailHandler.LastActivityTicks));
                if (Volatile.Read(ref YizukaThumbnailHandler.ActiveCalls) == 0 && idle > idleTimeout) break;
            }
            return 0;
        }
        finally { registrar.UnregisterTypeForComClients(cookie); }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
