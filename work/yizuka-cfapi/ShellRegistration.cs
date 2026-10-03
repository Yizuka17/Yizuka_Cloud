using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

internal static class ShellRegistration
{
    public static string Id => $"YizukaCloud!{WindowsIdentity.GetCurrent().User}!{(CloudClientConfig.IsRelease ? "CloudFiles" : "CfApiTest4")}";

    public static async Task RegisterAsync(string path)
    {
        if (!StorageProviderSyncRootManager.IsSupported())
            throw new PlatformNotSupportedException("Windows Shell cloud sync-root integration is unavailable.");
        var iconResource = Path.Combine(AppContext.BaseDirectory, "Yizuka.CloudFiles.exe") + ",0";
        try
        {
            var existing = StorageProviderSyncRootManager.GetSyncRootInformationForId(Id);
            if (existing is not null && string.Equals(existing.Path?.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Version == CloudClientConfig.Version && string.Equals(existing.IconResource, iconResource, StringComparison.OrdinalIgnoreCase)) return;
                StorageProviderSyncRootManager.Unregister(Id);
            }
            else if (existing is not null) throw new InvalidOperationException("A different sync root is already registered; move its files before changing the cache path.");
        }
        catch (System.Runtime.InteropServices.COMException) { }
        var info = new StorageProviderSyncRootInfo
        {
            Id = Id,
            DisplayNameResource = CloudClientConfig.IsRelease ? "Yizuka Cloud" : "Yizuka Cloud (test)",
            Path = await StorageFolder.GetFolderFromPathAsync(path),
            Context = CryptographicBuffer.ConvertStringToBinary(Id, BinaryStringEncoding.Utf8),
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            IconResource = iconResource,
            Version = CloudClientConfig.Version,
            ShowSiblingsAsGroup = false
        };
        StorageProviderSyncRootManager.Register(info);
    }

    public static void Unregister() => StorageProviderSyncRootManager.Unregister(Id);
}
