using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Text.Json;

internal sealed record RemoteEntry(string RelativePath, bool IsDirectory, long Length, DateTimeOffset? ModifiedUtc, string? ETag);

internal sealed class WebDavSource : IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private sealed record Endpoint(Uri BaseUri, HttpClient Client);
    private readonly Endpoint[] endpoints;
    private readonly bool automaticRouting;
    private readonly SemaphoreSlim routeLock = new(1, 1);
    private Endpoint? selected;
    private DateTime nextRouteCheckUtc;
    private readonly string remoteRoot;

    private WebDavSource(Uri? fixedEndpoint, string userName, string password, string remoteRoot, bool skipLocal, bool skipLan)
    {
        this.remoteRoot = Normalize(remoteRoot);
        automaticRouting = fixedEndpoint is null;
        var urls = fixedEndpoint is null
            ? new[] { "http://127.0.0.1:3924/", "https://17yizuka:8443/", "https://cloud.17yizuka.com/" }
                .Where((_, index) => !skipLocal || index != 0)
                .Where(url => !skipLan || url == "https://cloud.17yizuka.com/")
                .Select(x => new Uri(x))
            : new[] { fixedEndpoint };
        endpoints = urls.Select(url =>
        {
            var handler = new HttpClientHandler { UseProxy = url.Host is not ("127.0.0.1" or "localhost" or "17yizuka") };
            var client = new HttpClient(handler) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YizukaCloud-CfApi/0.7");
            return new Endpoint(url, client);
        }).ToArray();
        if (!automaticRouting) selected = endpoints[0];
    }

    public static WebDavSource FromInstalledClient(string remoteRoot = "", Uri? endpoint = null, bool skipLocal = false, bool skipLan = false)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YizukaCloud");
        var settings = File.ReadAllLines(Path.Combine(dataDir, "settings.ini"));
        var userName = settings.FirstOrDefault(x => x.StartsWith("username=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("The existing client has not been authorized.");
        var password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "password.dpapi")), null, DataProtectionScope.CurrentUser));
        return new WebDavSource(endpoint, userName, password, remoteRoot, skipLocal, skipLan);
    }

    private async Task<Endpoint> GetEndpointAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!automaticRouting) return endpoints[0];
        if (!force && selected is not null && DateTime.UtcNow < nextRouteCheckUtc) return selected;
        await routeLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && selected is not null && DateTime.UtcNow < nextRouteCheckUtc) return selected;
            foreach (var endpoint in endpoints)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), endpoint.BaseUri);
                    request.Headers.Add("Depth", "0");
                    using var response = await endpoint.Client.SendAsync(request, timeout.Token);
                    if (response.StatusCode != HttpStatusCode.MultiStatus) continue;
                    if (selected != endpoint) Console.WriteLine($"Cloud endpoint: {endpoint.BaseUri}");
                    selected = endpoint;
                    nextRouteCheckUtc = DateTime.UtcNow.AddSeconds(60);
                    return endpoint;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (HttpRequestException) { }
            }
            nextRouteCheckUtc = DateTime.UtcNow.AddSeconds(5);
            throw new HttpRequestException("No authorized Yizuka Cloud endpoint is reachable.");
        }
        finally { routeLock.Release(); }
    }

    public async Task<Uri> GetWebBaseUriAsync(CancellationToken cancellationToken = default) =>
        (await GetEndpointAsync(cancellationToken: cancellationToken)).BaseUri;

    private async Task<T> WithEndpointAsync<T>(Func<Endpoint, Task<T>> operation, CancellationToken cancellationToken)
    {
        var first = await GetEndpointAsync(cancellationToken: cancellationToken);
        try { return await operation(first); }
        catch (Exception error) when (automaticRouting && !cancellationToken.IsCancellationRequested &&
            (error is HttpRequestException requestError && (requestError.StatusCode is null or >= HttpStatusCode.InternalServerError) || error is TaskCanceledException))
        {
            var second = await GetEndpointAsync(force: true, cancellationToken);
            if (second == first) throw;
            return await operation(second);
        }
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string relativeDirectory, CancellationToken cancellationToken = default)
    {
        return await WithEndpointAsync<IReadOnlyList<RemoteEntry>>(async endpoint =>
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), ToUri(endpoint, relativeDirectory, true));
            request.Headers.Add("Depth", "1");
            request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
            using var response = await endpoint.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var prefix = Normalize(relativeDirectory);
            var results = new List<RemoteEntry>();
            foreach (var element in document.Descendants(Dav + "response"))
            {
                var href = element.Element(Dav + "href")?.Value;
                if (href is null) continue;
                var uri = new Uri(endpoint.BaseUri, href);
                if (!uri.AbsolutePath.StartsWith(endpoint.BaseUri.AbsolutePath, StringComparison.Ordinal)) continue;
                var fullPath = Uri.UnescapeDataString(uri.AbsolutePath[endpoint.BaseUri.AbsolutePath.Length..]).Trim('/');
                if (remoteRoot.Length > 0 && !fullPath.StartsWith(remoteRoot + "/", StringComparison.OrdinalIgnoreCase) && !fullPath.Equals(remoteRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var path = remoteRoot.Length == 0 ? fullPath : fullPath[remoteRoot.Length..].TrimStart('/');
                if (path.Length == 0 || path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                var prop = element.Descendants(Dav + "propstat").Select(x => x.Element(Dav + "prop")).FirstOrDefault(x => x is not null);
                if (prop is null) continue;
                var folder = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
                long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, out var length);
                DateTimeOffset.TryParse(prop.Element(Dav + "getlastmodified")?.Value, out var modified);
                results.Add(new RemoteEntry(path, folder, length, modified == default ? null : modified, prop.Element(Dav + "getetag")?.Value));
            }
            return results;
        }, cancellationToken);
    }

    public async Task<(string[] Keys, string? Timestamp)> InspectJsonListingAsync(CancellationToken cancellationToken = default)
    {
        return await WithEndpointAsync(async endpoint =>
        {
            using var response = await endpoint.Client.GetAsync(new Uri(ToUri(endpoint, "", true).AbsoluteUri + "?ls"), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var files = document.RootElement.GetProperty("files");
            if (files.GetArrayLength() == 0) return (Array.Empty<string>(), (string?)null);
            var item = files[0];
            return (item.EnumerateObject().Select(x => x.Name).ToArray(), item.TryGetProperty("ts", out var timestamp) ? timestamp.ToString() : null);
        }, cancellationToken);
    }

    public async Task<byte[]> ReadRangeAsync(string relativePath, long offset, int length, CancellationToken cancellationToken = default)
    {
        return await WithEndpointAsync(async endpoint =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ToUri(endpoint, relativePath, false));
            request.Headers.Range = new RangeHeaderValue(offset, checked(offset + length - 1));
            using var response = await endpoint.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.OK && offset != 0) throw new InvalidOperationException("Server ignored a nonzero range request.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length < length) throw new IOException($"Short remote read: expected {length}, received {bytes.Length}.");
            return bytes.Length == length ? bytes : bytes[..length];
        }, cancellationToken);
    }

    public async Task<(string ContentType, byte[] Data)> GetThumbnailAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        return await WithEndpointAsync(async endpoint =>
        {
            var url = new Uri(ToUri(endpoint, relativePath, false).AbsoluteUri + "?th=j");
            using var response = await endpoint.Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return (response.Content.Headers.ContentType?.MediaType ?? "", await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await WithEndpointAsync(async endpoint =>
        {
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), ToUri(endpoint, relativePath, true));
            using var response = await endpoint.Client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
            {
                var parent = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
                var entries = await ListAsync(parent, cancellationToken);
                if (entries.Any(x => x.IsDirectory && x.RelativePath.Equals(Normalize(relativePath), StringComparison.OrdinalIgnoreCase))) return true;
            }
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task PutAsync(string relativePath, byte[] data, CancellationToken cancellationToken = default)
    {
        await WithEndpointAsync(async endpoint =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, ToUri(endpoint, relativePath, false)) { Content = new ByteArrayContent(data) };
            using var response = await endpoint.Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task PutFileAsync(string relativePath, string filePath, bool forceChunked = false, CancellationToken cancellationToken = default)
    {
        await WithEndpointAsync(async endpoint =>
        {
            var file = new FileInfo(filePath);
            if (forceChunked || file.Length > 64L * 1024 * 1024 || endpoint.BaseUri.Host == "cloud.17yizuka.com")
            {
                var lastSlash = relativePath.LastIndexOf('/');
                var folder = lastSlash < 0 ? "" : relativePath[..lastSlash];
                var name = lastSlash < 0 ? relativePath : relativePath[(lastSlash + 1)..];
                await Up2kUploader.UploadAsync(endpoint.Client, ToUri(endpoint, folder, true), filePath, name, cancellationToken);
                return true;
            }
            await using var stream = file.OpenRead();
            using var request = new HttpRequestMessage(HttpMethod.Put, ToUri(endpoint, relativePath, false)) { Content = new StreamContent(stream) };
            using var response = await endpoint.Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await WithEndpointAsync(async endpoint =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, ToUri(endpoint, relativePath, false));
            using var response = await endpoint.Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task MoveAsync(string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        await WithEndpointAsync(async endpoint =>
        {
            using var request = new HttpRequestMessage(new HttpMethod("MOVE"), ToUri(endpoint, sourceRelativePath, false));
            request.Headers.Add("Destination", ToUri(endpoint, destinationRelativePath, false).AbsoluteUri);
            request.Headers.Add("Overwrite", "F");
            using var response = await endpoint.Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    private Uri ToUri(Endpoint endpoint, string relativePath, bool directory)
    {
        var path = Normalize(relativePath);
        if (remoteRoot.Length > 0) path = remoteRoot + (path.Length > 0 ? "/" + path : "");
        var encoded = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        if (directory && encoded.Length > 0) encoded += "/";
        return new Uri(endpoint.BaseUri, encoded);
    }

    private static string Normalize(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').Trim('/');
        if (path.Split('/').Any(x => x is ".." or ".")) throw new ArgumentException("Relative path traversal is not permitted.", nameof(relativePath));
        return path;
    }

    public void Dispose()
    {
        foreach (var endpoint in endpoints) endpoint.Client.Dispose();
        routeLock.Dispose();
    }
}
