using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Copyparty's up2k protocol: hash the file, handshake, send missing chunks, handshake again.
internal static class Up2kUploader
{
    private sealed record Chunk(string Hash, long Offset, int Length);

    public static async Task UploadAsync(HttpClient client, Uri folder, string filePath, string name, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        var chunks = await HashChunksAsync(file, cancellationToken);
        var hashes = chunks.Select(x => x.Hash).ToArray();
        var lastModified = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();
        var requestBody = new { hash = hashes, name, lmod = lastModified, size = file.Length, replace = true };
        var attempts = 0;
        while (true)
        {
            var response = await HandshakeAsync(client, folder, requestBody, cancellationToken);
            var missing = response.GetProperty("hash").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (missing.Length == 0) return;
            if (++attempts > 4) throw new IOException("up2k upload did not verify after four handshakes.");
            var wark = response.GetProperty("wark").GetString() ?? throw new IOException("up2k handshake lacked a wark.");
            var purl = response.GetProperty("purl").GetString() ?? throw new IOException("up2k handshake lacked purl.");
            var uploadUrl = new Uri(client.BaseAddress!, purl.TrimStart('/'));
            foreach (var hash in missing.Distinct(StringComparer.Ordinal))
            {
                var chunk = chunks.FirstOrDefault(x => x.Hash == hash) ?? throw new IOException("up2k requested an unknown chunk.");
                await SendChunkAsync(client, uploadUrl, filePath, chunk, wark, cancellationToken);
            }
        }
    }

    private static async Task<JsonElement> HandshakeAsync(HttpClient client, Uri folder, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, folder)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task<List<Chunk>> HashChunksAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var chunks = new List<Chunk>();
        var chunkSize = ChunkSize(file.Length);
        await using var stream = file.OpenRead();
        var buffer = new byte[1024 * 1024];
        for (long offset = 0; offset < file.Length; offset += chunkSize)
        {
            var length = checked((int)Math.Min(chunkSize, file.Length - offset));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("File changed during up2k hashing.");
                sha.AppendData(buffer, 0, read);
                remaining -= read;
            }
            chunks.Add(new Chunk(Convert.ToBase64String(sha.GetHashAndReset().AsSpan(0, 33)).Replace('+', '-').Replace('/', '_'), offset, length));
        }
        if (file.Length != stream.Length) throw new IOException("File changed during up2k hashing.");
        return chunks;
    }

    private static int ChunkSize(long fileSize)
    {
        long size = 1024 * 1024, step = 512 * 1024;
        while (true)
        {
            foreach (var multiplier in new[] { 1, 2 })
            {
                var count = (fileSize + size - 1) / size;
                if (count <= 256 || (size >= 32L * 1024 * 1024 && count <= 4096))
                    return checked((int)Math.Min(size, 1024L * 1024 * 1024));
                size += step;
                step *= multiplier;
            }
        }
    }

    private static async Task SendChunkAsync(HttpClient client, Uri uploadUrl, string filePath, Chunk chunk, string wark, CancellationToken cancellationToken)
    {
        const int maxSubchunk = 16 * 1024 * 1024;
        await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        for (var subOffset = 0; subOffset < chunk.Length; subOffset += maxSubchunk)
        {
            var length = Math.Min(maxSubchunk, chunk.Length - subOffset);
            var bytes = new byte[length];
            stream.Position = chunk.Offset + subOffset;
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = new ByteArrayContent(bytes) };
            request.Headers.Add("X-Up2k-Hash", chunk.Hash);
            request.Headers.Add("X-Up2k-Wark", wark);
            if (chunk.Length > maxSubchunk) request.Headers.Add("X-Up2k-Subc", subOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
