using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Model.QQ;

using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQMaterialClient(QQApiTransport transport, HttpClient uploadHttp) : IQOfficialMaterialApi
{
    private const int MaxImageSize = 10 * 1024 * 1024;

    public async Task<QOfficialImageMaterial> UploadImageAsync(Stream content, string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!content.CanRead) throw new ArgumentException("Image stream must be readable.", nameof(content));
        if (Path.GetFileName(fileName) != fileName || fileName.Contains('\\') || fileName.Contains('/'))
            throw new ArgumentException("Image file name must not contain a path.", nameof(fileName));
        var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif", ".webp" => "image/webp",
            _ => throw new ArgumentException("Image must be PNG, JPEG, GIF or WebP.", nameof(fileName))
        };
        // Bounded buffering supports non-seekable streams and hashes exactly the bytes sent to COS.
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImageSize)
                throw new ArgumentException("QQ Markdown images must not exceed 10 MB.", nameof(content));
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw new ArgumentException("Image stream is empty.", nameof(content));
        var bytes = buffer.GetBuffer();
        var length = checked((int)buffer.Length);
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes.AsSpan(0, length)));
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes.AsSpan(0, length)));
        using var prepare = await transport.SendAsync(HttpMethod.Post, "v2/media-images/prepare", new JsonObject
        {
            ["file_name"] = fileName, ["file_size"] = length, ["content_type"] = contentType,
            ["md5"] = md5, ["sha1"] = sha1
        }, cancellationToken).ConfigureAwait(false);
        var prepared = await ReadAsync(prepare, cancellationToken).ConfigureAwait(false);
        var uploadId = Required(prepared, "upload_id");
        if (!Uri.TryCreate(Required(prepared, "presigned_url"), UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidDataException("QQ material upload requires a valid HTTPS upload URL.");
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Content = new ByteArrayContent(bytes, 0, length);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Deliberately bypass QQApiTransport: never send the bot access token to COS.
        using var uploaded = await uploadHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        uploaded.EnsureSuccessStatusCode();
        var etag = uploaded.Headers.ETag?.Tag.Trim('"');
        if (etag is null || !string.Equals(etag, md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("QQ material upload returned a missing or mismatched MD5 ETag.");
        using var complete = await transport.SendAsync(HttpMethod.Post, "v2/media-images/complete",
            new JsonObject { ["upload_id"] = uploadId, ["md5"] = etag }, cancellationToken).ConfigureAwait(false);
        return Map(await ReadAsync(complete, cancellationToken).ConfigureAwait(false)) with { FileName = fileName };
    }

    public async Task<QOfficialImageMaterialPage> GetImagesAsync(string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var route = "v2/media-images" + (string.IsNullOrEmpty(cursor) ? "" : "?cursor=" + Uri.EscapeDataString(cursor));
        using var response = await transport.SendAsync(HttpMethod.Get, route, null, cancellationToken).ConfigureAwait(false);
        var value = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var images = value.GetProperty("images").EnumerateArray().Select(Map).ToArray();
        return new(images, QQOfficialGroupMapper.Text(value, "next_cursor"), value.GetProperty("total").GetUInt64());
    }

    public async Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        using var response = await transport.SendAsync(HttpMethod.Delete,
            $"v2/media-images/{Uri.EscapeDataString(imageId)}/delete", null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(QQJson.Options, cancellationToken).ConfigureAwait(false);

    private static string Required(JsonElement value, string key) =>
        QQOfficialGroupMapper.Text(value, key) is { Length: > 0 } text ? text
            : throw new InvalidDataException($"QQ material response has no {key}.");

    private static QOfficialImageMaterial Map(JsonElement value)
    {
        var url = Required(value, "url");
        if (!url.StartsWith("qqbot-media://image/v1/", StringComparison.Ordinal))
            throw new InvalidDataException("QQ material response has an invalid image reference.");
        return new(Required(value, "image_id"), url)
        {
            FileName = QQOfficialGroupMapper.Text(value, "file_name"),
            PreviewUrl = QQOfficialGroupMapper.Text(value, "preview_url"),
            Ttl = value.TryGetProperty("ttl", out var ttl) ? ttl.GetInt64() : null,
            CreatedAt = Time(value, "created_at"), ExpiresAt = Time(value, "expires_at")
        };
    }

    private static DateTimeOffset? Time(JsonElement value, string key) =>
        value.TryGetProperty(key, out var time) ? DateTimeOffset.FromUnixTimeSeconds(time.GetInt64()) : null;
}
