internal static class CloudClientConfig
{
    public static bool IsRelease
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current.Id.Name == "Yizuka.CloudFiles"; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), IsRelease ? "Yizuka Cloud Files" : "Yizuka Cloud Test");
    public static string RemoteRoot => IsRelease ? "" : "CFAPI端到端测试";
    public static string Version => IsRelease ? "1.0.6" : "0.21.0";
    public static string ControlPipe => IsRelease ? "YizukaCloud.CfApi.Release.Control" : "YizukaCloud.CfApiTest4.Control";
    public static string SingletonName => IsRelease ? "Local\\YizukaCloud.CfApi.Release.Service" : "Local\\YizukaCloud.CfApiTest4.Service";
    public static string ServiceLogName => IsRelease ? "cfapi-release-service.log" : "cfapi-service.log";

    public static string Root
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", IsRelease ? "cloudfiles.ini" : "cloudfiles-test.ini");
            if (!File.Exists(path)) return DefaultRoot;
            var configured = File.ReadAllLines(path).FirstOrDefault(line => line.StartsWith("root=", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            return string.IsNullOrWhiteSpace(configured) ? DefaultRoot : Path.GetFullPath(configured);
        }
    }

    public static void EnsureSafeRoot(string path)
    {
        var unsafePath = CloudRootSafety.UnsafeReparseAncestor(path);
        if (unsafePath is not null)
            throw new IOException($"Cloud cache path traverses a junction or reparse point: {unsafePath}");
    }
}
