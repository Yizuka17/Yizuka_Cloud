using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

internal sealed class CloudMirror : IDisposable
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenMetadataHandle(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    private readonly string root;
    private readonly WebDavSource server;
    private readonly bool unregisterOnDispose;
    private readonly bool registerRoot;
    private readonly CF_CALLBACK fetchData;
    private readonly CF_CALLBACK_REGISTRATION[] callbacks;
    private readonly ConcurrentDictionary<string, (long LocalLength, DateTime LocalModifiedUtc, long RemoteLength, DateTime? RemoteModifiedUtc)> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> pendingDirectoryDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> knownDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> suppressedDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string OldPath, string NewPath)> pendingDirectoryMoves = new();
    private readonly string journalPath;
    private readonly string snapshotPath;
    private readonly object journalGate = new();
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? pendingSync;
    private bool disposed;
    private CF_CONNECTION_KEY connection;
    private bool registered;
    private bool connected;
    public bool InitialOnline { get; private set; }

    public CloudMirror(string root, WebDavSource server, bool unregisterOnDispose = true, bool registerRoot = true)
    {
        this.root = root;
        this.server = server;
        this.unregisterOnDispose = unregisterOnDispose;
        this.registerRoot = registerRoot;
        var rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        journalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "CfApiJournals", rootKey + ".json");
        snapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", "CfApiJournals", rootKey + ".snapshot.json");
        LoadSnapshot();
        LoadJournal();
        fetchData = FetchData;
        callbacks =
        [
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = fetchData },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        ];
    }

    public async Task StartAsync(bool allowOffline = false)
    {
        CloudClientConfig.EnsureSafeRoot(root);
        void Stage(string stage)
        {
            if (CloudClientConfig.IsRelease)
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName), $"{DateTime.Now:O} mirror {stage}{Environment.NewLine}");
        }
        Directory.CreateDirectory(root);
        var registration = new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = CloudClientConfig.IsRelease ? "Yizuka Cloud" : "Yizuka Cloud Test",
            ProviderVersion = CloudClientConfig.Version,
            ProviderId = new Guid(CloudClientConfig.IsRelease ? "491C1D9D-B311-4AF2-B45F-B9EE65EBE713" : "84631f0d-40a8-4d9a-b306-31b633a28866")
        };
        var policies = new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new CF_HYDRATION_POLICY
            {
                Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PARTIAL,
                Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED
            },
            Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL },
            InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME,
            HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE
        };
        if (registerRoot)
        {
            Stage("register begin");
            try { CfRegisterSyncRoot(root, in registration, in policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE).ThrowIfFailed(); }
            catch (Exception firstError)
            {
                try { CfRegisterSyncRoot(root, in registration, in policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowIfFailed(); }
                catch (Exception updateError) { throw new InvalidOperationException($"CFAPI register root failed: initial={firstError.Message}; update={updateError.Message}", updateError); }
            }
        }
        registered = registerRoot;
        Stage("connect begin");
        try { CfConnectSyncRoot(root, callbacks, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out connection).ThrowIfFailed(); }
        catch (Exception error) { throw new InvalidOperationException($"CFAPI connect root failed: {error.Message}", error); }
        connected = true;
        Stage("populate begin");
        try
        {
            await PopulateDirectoryAsync("");
            InitialOnline = true;
            Stage("populate complete");
            SaveSnapshot();
        }
        catch (IOException error) when (allowOffline && error.InnerException is HttpRequestException or TaskCanceledException or IOException)
        {
            Stage($"offline startup: {error.InnerException.Message}");
        }
    }

    private async Task PopulateDirectoryAsync(string relativeDirectory)
    {
        if (CloudClientConfig.IsRelease)
            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud", CloudClientConfig.ServiceLogName), $"{DateTime.Now:O} list {relativeDirectory}{Environment.NewLine}");
        IReadOnlyList<RemoteEntry> entries;
        try { entries = await server.ListAsync(relativeDirectory); }
        catch (Exception error) { throw new IOException($"Unable to list cloud directory '{relativeDirectory}'.", error); }
        var localDirectory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) knownDirectories.TryAdd(entry.RelativePath, 0);
            if (!entry.IsDirectory)
                knownFiles.TryAdd(entry.RelativePath, (entry.Length, (entry.ModifiedUtc ?? DateTimeOffset.UtcNow).UtcDateTime, entry.Length, entry.ModifiedUtc?.UtcDateTime));
            if (pendingDeletes.ContainsKey(entry.RelativePath) || pendingDirectoryDeletes.Keys.Any(path =>
                entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                entry.RelativePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) || pendingDirectoryMoves.Any(move =>
                entry.RelativePath.Equals(move.OldPath, StringComparison.OrdinalIgnoreCase) ||
                entry.RelativePath.StartsWith(move.OldPath + "/", StringComparison.OrdinalIgnoreCase))) continue;
            var entryName = entry.RelativePath[(relativeDirectory.Length > 0 ? relativeDirectory.Length + 1 : 0)..];
            if (entryName.Contains('/')) continue;
            var localPath = Path.Combine(localDirectory, entryName);
            if (File.Exists(localPath)) continue;
            if (Directory.Exists(localPath))
            {
                if (entry.IsDirectory) await PopulateDirectoryAsync(entry.RelativePath);
                continue;
            }
            var pathIdentity = Marshal.StringToHGlobalUni(entry.RelativePath);
            try
            {
                var fileTimeNumber = (entry.ModifiedUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToFileTimeUtc();
                var fileTime = new System.Runtime.InteropServices.ComTypes.FILETIME
                {
                    dwLowDateTime = unchecked((int)fileTimeNumber),
                    dwHighDateTime = unchecked((int)(fileTimeNumber >> 32))
                };
                var info = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = entryName,
                    FileIdentity = pathIdentity,
                    FileIdentityLength = (uint)((entry.RelativePath.Length + 1) * 2),
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = entry.IsDirectory ? 0 : entry.Length,
                        BasicInfo = new Vanara.PInvoke.Kernel32.FILE_BASIC_INFO
                        {
                            FileAttributes = entry.IsDirectory ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                            CreationTime = fileTime,
                            LastWriteTime = fileTime,
                            LastAccessTime = fileTime,
                            ChangeTime = fileTime
                        }
                    },
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC |
                            (entry.IsDirectory ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0)
                };
                var batch = new[] { info };
                CfCreatePlaceholders(localDirectory, batch, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out var processed).ThrowIfFailed();
                if (processed != 1) throw new IOException($"Could not create cloud placeholder: {entry.RelativePath}");
                batch[0].Result.ThrowIfFailed();
            }
            finally { Marshal.FreeHGlobal(pathIdentity); }
            if (entry.IsDirectory) await PopulateDirectoryAsync(entry.RelativePath);
        }
    }

    public async Task RefreshNewRemoteFilesAsync()
    {
        await syncLock.WaitAsync();
        try
        {
            var remote = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            await CollectRemoteAsync("", remote);
            foreach (var pair in knownFiles.ToArray())
            {
                var relative = pair.Key;
                var previous = pair.Value;
                var local = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var stillRemote = remote.TryGetValue(relative, out var current);
                if (stillRemote && MatchesRemoteVersion(previous, current!)) continue;
                if (pendingDeletes.ContainsKey(relative)) continue;
                if (File.Exists(local))
                {
                    var file = new FileInfo(local);
                    if (file.Length != previous.LocalLength || file.LastWriteTimeUtc != previous.LocalModifiedUtc)
                    {
                        Console.Error.WriteLine($"Conflict: local file changed while server {(stillRemote ? "updated" : "deleted")}: {relative}");
                        continue;
                    }
                    suppressedDeletes[relative] = DateTime.UtcNow.AddSeconds(10);
                    try { File.Delete(local); }
                    catch (IOException error) { Console.Error.WriteLine($"Remote refresh deferred for open file {relative}: {error.Message}"); continue; }
                }
                knownFiles.TryRemove(relative, out _);
            }
            await PopulateDirectoryAsync("");
            SaveSnapshot();
        }
        finally { syncLock.Release(); }
    }

    private async Task CollectRemoteAsync(string directory, Dictionary<string, RemoteEntry> result)
    {
        foreach (var entry in await server.ListAsync(directory))
        {
            result[entry.RelativePath] = entry;
            if (entry.IsDirectory) await CollectRemoteAsync(entry.RelativePath, result);
        }
    }

    // Test-folder write-back. Kept explicit until rename/conflict behavior is hardened.
    public async Task SyncLocalChangesAsync()
    {
        await syncLock.WaitAsync();
        try
        {
            while (pendingDirectoryMoves.TryPeek(out var move))
            {
                await ApplyDirectoryMoveAsync(move.OldPath, move.NewPath);
                pendingDirectoryMoves.TryDequeue(out _);
                SaveJournal();
            }
            foreach (var directory in pendingDirectoryDeletes.Keys.ToArray())
                await ApplyDirectoryDeleteAsync(directory);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                if (knownDirectories.ContainsKey(relative)) continue;
                await server.CreateDirectoryAsync(relative);
                knownDirectories.TryAdd(relative, 0);
            }
            var localFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                localFiles.Add(relative);
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.Offline)) continue;
                var info = new FileInfo(file);
                var state = (info.Length, info.LastWriteTimeUtc);
                if (knownFiles.TryGetValue(relative, out var previous) && (previous.LocalLength, previous.LocalModifiedUtc) == state) continue;
                var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
                var remoteBefore = (await server.ListAsync(parent)).FirstOrDefault(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (knownFiles.TryGetValue(relative, out previous) && remoteBefore is not null && !MatchesRemoteVersion(previous, remoteBefore))
                {
                    Console.Error.WriteLine($"Conflict: server changed after local placeholder was created: {relative}");
                    continue;
                }
                await server.PutFileAsync(relative, file);
                var remoteAfter = (await server.ListAsync(parent)).FirstOrDefault(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                knownFiles[relative] = (state.Length, state.LastWriteTimeUtc, remoteAfter?.Length ?? state.Length, remoteAfter?.ModifiedUtc?.UtcDateTime);
            }
            foreach (var removed in pendingDeletes.Keys.ToArray())
            {
                if (localFiles.Contains(removed) || !knownFiles.TryGetValue(removed, out var previous))
                {
                    pendingDeletes.TryRemove(removed, out _);
                    SaveJournal();
                    continue;
                }
                var parent = Path.GetDirectoryName(removed.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
                var remote = (await server.ListAsync(parent)).FirstOrDefault(x => x.RelativePath.Equals(removed, StringComparison.OrdinalIgnoreCase));
                if (remote is null)
                {
                    knownFiles.TryRemove(removed, out _);
                    pendingDeletes.TryRemove(removed, out _);
                    SaveJournal();
                    continue;
                }
                // If the server changed since this placeholder was created, preserve its newer version.
                if (!MatchesRemoteVersion(previous, remote))
                {
                    pendingDeletes.TryRemove(removed, out _);
                    SaveJournal();
                    if (previous.RemoteModifiedUtc is not null)
                    {
                        knownFiles.TryRemove(removed, out _);
                        await PopulateDirectoryAsync(parent);
                    }
                    else Console.Error.WriteLine($"Delete withheld because server version is unknown: {removed}");
                    continue;
                }
                await server.DeleteAsync(removed);
                knownFiles.TryRemove(removed, out _);
                pendingDeletes.TryRemove(removed, out _);
                SaveJournal();
            }
            SaveSnapshot();
        }
        finally { syncLock.Release(); }
    }

    private static bool MatchesRemoteVersion((long LocalLength, DateTime LocalModifiedUtc, long RemoteLength, DateTime? RemoteModifiedUtc) previous, RemoteEntry remote)
        => previous.RemoteModifiedUtc is not null &&
           remote.ModifiedUtc?.UtcDateTime == previous.RemoteModifiedUtc &&
           remote.Length == previous.RemoteLength;

    private async Task ApplyDirectoryMoveAsync(string oldRelative, string newRelative)
    {
        var localNew = Path.Combine(root, newRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(localNew)) return;
        var oldParent = Path.GetDirectoryName(oldRelative.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        var newParent = Path.GetDirectoryName(newRelative.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        var oldEntry = (await server.ListAsync(oldParent)).FirstOrDefault(x => x.RelativePath.Equals(oldRelative, StringComparison.OrdinalIgnoreCase));
        var newEntry = (await server.ListAsync(newParent)).FirstOrDefault(x => x.RelativePath.Equals(newRelative, StringComparison.OrdinalIgnoreCase));
        if (oldEntry is not null)
        {
            if (newEntry is not null) throw new IOException($"Remote folder already exists: {newRelative}");
        }
        else if (newEntry is null) throw new IOException($"Remote folder to rename was not found: {oldRelative}");

        foreach (var item in Directory.EnumerateFileSystemEntries(localNew, "*", SearchOption.AllDirectories).Prepend(localNew))
        {
            if (Directory.Exists(item)) continue;
            var relative = Path.GetRelativePath(root, item).Replace('\\', '/');
            using var handle = OpenMetadataHandle(item, 0x00040000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open cloud metadata for {relative}");
            {
                var identity = Encoding.Unicode.GetBytes(relative + '\0');
                var identityBuffer = Marshal.AllocHGlobal(identity.Length);
                try
                {
                    Marshal.Copy(identity, 0, identityBuffer, identity.Length);
                    try
                    {
                        CfUpdatePlaceholder(new HFILE(handle.DangerousGetHandle()), IntPtr.Zero, identityBuffer, (uint)identity.Length,
                            null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE, IntPtr.Zero, IntPtr.Zero).ThrowIfFailed();
                        Console.WriteLine($"Updated cloud identity: {relative}");
                    }
                    catch (Exception error) { throw new IOException($"Cloud identity update failed for {relative}", error); }
                }
                finally { Marshal.FreeHGlobal(identityBuffer); }
            }
        }
        if (oldEntry is not null) await server.MoveAsync(oldRelative, newRelative);
        foreach (var pair in knownFiles.ToArray())
        {
            if (!pair.Key.StartsWith(oldRelative + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var renamed = newRelative + pair.Key[oldRelative.Length..];
            if (knownFiles.TryRemove(pair.Key, out var value)) knownFiles[renamed] = value;
        }
        foreach (var key in pendingDeletes.Keys.Where(x => x.StartsWith(oldRelative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            pendingDeletes.TryRemove(key, out _);
        foreach (var key in knownDirectories.Keys.Where(x => x.Equals(oldRelative, StringComparison.OrdinalIgnoreCase) || x.StartsWith(oldRelative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            if (knownDirectories.TryRemove(key, out _)) knownDirectories.TryAdd(newRelative + key[oldRelative.Length..], 0);
    }

    private async Task ApplyDirectoryDeleteAsync(string relative)
    {
        var local = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(local))
        {
            pendingDirectoryDeletes.TryRemove(relative, out _);
            SaveJournal();
            return;
        }
        var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        var remoteFolder = (await server.ListAsync(parent)).FirstOrDefault(x => x.IsDirectory && x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (remoteFolder is not null)
        {
            var remote = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            await CollectRemoteAsync(relative, remote);
            var changed = remote.Values.Any(entry => entry.IsDirectory
                ? !knownDirectories.ContainsKey(entry.RelativePath)
                : !knownFiles.TryGetValue(entry.RelativePath, out var expected) || !MatchesRemoteVersion(expected, entry));
            if (changed)
            {
                Console.Error.WriteLine($"Directory deletion withheld because server contents changed: {relative}");
                pendingDirectoryDeletes.TryRemove(relative, out _);
                foreach (var key in pendingDeletes.Keys.Where(x => x.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                    pendingDeletes.TryRemove(key, out _);
                SaveJournal();
                await PopulateDirectoryAsync(parent);
                return;
            }
            await server.DeleteAsync(relative);
        }
        foreach (var key in knownFiles.Keys.Where(x => x.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            knownFiles.TryRemove(key, out _);
        foreach (var key in knownDirectories.Keys.Where(x => x.Equals(relative, StringComparison.OrdinalIgnoreCase) || x.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            knownDirectories.TryRemove(key, out _);
        foreach (var key in pendingDeletes.Keys.Where(x => x.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            pendingDeletes.TryRemove(key, out _);
        pendingDirectoryDeletes.TryRemove(relative, out _);
        SaveJournal();
    }

    public void StartWatching()
    {
        if (watcher is not null) return;
        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Changed += (_, _) => ScheduleSync();
        watcher.Created += (_, _) => ScheduleSync();
        watcher.Deleted += (_, eventArgs) => { QueueDelete(eventArgs.FullPath); ScheduleSync(); };
        watcher.Renamed += (_, eventArgs) =>
        {
            if (Directory.Exists(eventArgs.FullPath)) QueueDirectoryMove(eventArgs.OldFullPath, eventArgs.FullPath);
            else QueueDelete(eventArgs.OldFullPath);
            ScheduleSync();
        };
    }

    public void StopWatching()
    {
        watcher?.Dispose();
        watcher = null;
    }

    private void QueueDelete(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (suppressedDeletes.TryRemove(relative, out var until) && DateTime.UtcNow <= until) return;
        if (knownDirectories.ContainsKey(relative)) pendingDirectoryDeletes.TryAdd(relative, 0);
        else if (knownFiles.ContainsKey(relative)) pendingDeletes.TryAdd(relative, 0);
        SaveJournal();
    }

    public void MarkLocalDeletionForTest(string fullPath) => QueueDelete(fullPath);

    public void MarkDirectoryMoveForTest(string oldPath, string newPath) => QueueDirectoryMove(oldPath, newPath);

    private void QueueDirectoryMove(string oldPath, string newPath)
    {
        var oldRelative = Path.GetRelativePath(root, oldPath).Replace('\\', '/');
        var newRelative = Path.GetRelativePath(root, newPath).Replace('\\', '/');
        pendingDirectoryMoves.Enqueue((oldRelative, newRelative));
        SaveJournal();
    }

    private sealed class JournalState
    {
        public string[] Deletes { get; set; } = [];
        public string[] DirectoryDeletes { get; set; } = [];
        public string[] KnownDirectories { get; set; } = [];
        public DirectoryMove[] Moves { get; set; } = [];
        public PendingVersion[] Versions { get; set; } = [];
    }

    private sealed class SnapshotState
    {
        public PendingVersion[] Files { get; set; } = [];
        public string[] Directories { get; set; } = [];
    }

    private sealed class PendingVersion
    {
        public string Path { get; set; } = "";
        public long LocalLength { get; set; }
        public DateTime LocalModifiedUtc { get; set; }
        public long RemoteLength { get; set; }
        public DateTime? RemoteModifiedUtc { get; set; }
    }

    private sealed class DirectoryMove
    {
        public string OldPath { get; set; } = "";
        public string NewPath { get; set; } = "";
    }

    private void LoadJournal()
    {
        if (!File.Exists(journalPath)) return;
        var state = JsonSerializer.Deserialize<JournalState>(File.ReadAllText(journalPath)) ?? new JournalState();
        foreach (var version in state.Versions)
            knownFiles[version.Path] = (version.LocalLength, version.LocalModifiedUtc, version.RemoteLength, version.RemoteModifiedUtc);
        foreach (var path in state.Deletes) pendingDeletes.TryAdd(path, 0);
        foreach (var path in state.DirectoryDeletes) pendingDirectoryDeletes.TryAdd(path, 0);
        foreach (var path in state.KnownDirectories) knownDirectories.TryAdd(path, 0);
        foreach (var move in state.Moves) pendingDirectoryMoves.Enqueue((move.OldPath, move.NewPath));
    }

    private void LoadSnapshot()
    {
        if (!File.Exists(snapshotPath)) return;
        var state = JsonSerializer.Deserialize<SnapshotState>(File.ReadAllText(snapshotPath)) ?? new SnapshotState();
        foreach (var file in state.Files)
            knownFiles[file.Path] = (file.LocalLength, file.LocalModifiedUtc, file.RemoteLength, file.RemoteModifiedUtc);
        foreach (var directory in state.Directories) knownDirectories.TryAdd(directory, 0);
    }

    private void SaveSnapshot()
    {
        lock (journalGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            var state = new SnapshotState
            {
                Files = knownFiles.Select(pair => new PendingVersion { Path = pair.Key, LocalLength = pair.Value.LocalLength,
                    LocalModifiedUtc = pair.Value.LocalModifiedUtc, RemoteLength = pair.Value.RemoteLength,
                    RemoteModifiedUtc = pair.Value.RemoteModifiedUtc }).ToArray(),
                Directories = knownDirectories.Keys.ToArray()
            };
            var temporary = snapshotPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, snapshotPath, true);
        }
    }

    private void SaveJournal()
    {
        lock (journalGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            var state = new JournalState
            {
                Deletes = pendingDeletes.Keys.ToArray(),
                DirectoryDeletes = pendingDirectoryDeletes.Keys.ToArray(),
                KnownDirectories = knownDirectories.Keys.Where(path => pendingDirectoryDeletes.Keys.Any(deleted =>
                    path.Equals(deleted, StringComparison.OrdinalIgnoreCase) || path.StartsWith(deleted + "/", StringComparison.OrdinalIgnoreCase))).ToArray(),
                Moves = pendingDirectoryMoves.Select(move => new DirectoryMove { OldPath = move.OldPath, NewPath = move.NewPath }).ToArray(),
                Versions = pendingDeletes.Keys
                    .Concat(pendingDirectoryMoves.SelectMany(move => knownFiles.Keys.Where(path => path.StartsWith(move.OldPath + "/", StringComparison.OrdinalIgnoreCase))))
                    .Concat(pendingDirectoryDeletes.Keys.SelectMany(deleted => knownFiles.Keys.Where(path => path.StartsWith(deleted + "/", StringComparison.OrdinalIgnoreCase))))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(path => knownFiles.ContainsKey(path))
                    .Select(path =>
                    {
                        var known = knownFiles[path];
                        return new PendingVersion { Path = path, LocalLength = known.LocalLength, LocalModifiedUtc = known.LocalModifiedUtc,
                            RemoteLength = known.RemoteLength, RemoteModifiedUtc = known.RemoteModifiedUtc };
                    }).ToArray()
            };
            var temporary = journalPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, journalPath, true);
        }
    }

    private void ScheduleSync()
    {
        if (disposed) return;
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref pendingSync, current);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, current.Token);
                await SyncLocalChangesAsync();
                Console.WriteLine("Local changes synced.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Console.Error.WriteLine($"Local sync failed; will retry on next change: {error.Message}"); }
            finally { current.Dispose(); }
        });
    }

    private void FetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        var request = parameters.GetParam<CF_CALLBACK_PARAMETERS.FETCHDATA>();
        Console.WriteLine($"FETCH_DATA requested offset={request.RequiredFileOffset} length={request.RequiredLength}");
        var path = Marshal.PtrToStringUni(info.FileIdentity, checked((int)info.FileIdentityLength / 2))?.TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(path))
        {
            Transfer(info, request.RequiredFileOffset, Array.Empty<byte>(), NTStatus.STATUS_INVALID_PARAMETER);
            return;
        }
        try
        {
            var offset = request.RequiredFileOffset;
            var remaining = request.RequiredLength;
            while (remaining > 0)
            {
                var size = checked((int)Math.Min(remaining, 4 * 1024 * 1024));
                var bytes = server.ReadRangeAsync(path, offset, size).GetAwaiter().GetResult();
                Transfer(info, offset, bytes, NTStatus.STATUS_SUCCESS);
                offset += bytes.Length;
                remaining -= bytes.Length;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FETCH_DATA {path}: {error.Message}");
            try { Transfer(info, request.RequiredFileOffset, Array.Empty<byte>(), NTStatus.STATUS_NETWORK_UNREACHABLE); }
            catch (Exception transferError) { Console.Error.WriteLine($"FETCH_DATA completion failed: {transferError.Message}"); }
        }
    }

    private static void Transfer(in CF_CALLBACK_INFO info, long offset, byte[] data, NTStatus status)
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        try
        {
            if (data.Length > 0) Marshal.Copy(data, 0, buffer, data.Length);
            var operation = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = info.ConnectionKey,
                TransferKey = info.TransferKey,
                RequestKey = info.RequestKey
            };
            var parameters = CF_OPERATION_PARAMETERS.Create(new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Buffer = buffer,
                Offset = offset,
                Length = data.Length,
                CompletionStatus = status
            });
            CfExecute(operation, ref parameters).ThrowIfFailed();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watcher?.Dispose();
        try { pendingSync?.Cancel(); } catch (ObjectDisposedException) { }
        if (connected) CfDisconnectSyncRoot(connection);
        if (registered && unregisterOnDispose) CfUnregisterSyncRoot(root);
        syncLock.Dispose();
    }
}
