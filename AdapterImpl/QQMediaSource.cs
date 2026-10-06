using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQMediaSource(string? path, Uri? url, string fileName, bool temporary = false) : IDisposable
{
    private const long MaxFileSize = 200L * 1024 * 1024;

    public string? Path { get; } = path;
    public Uri? Url { get; } = url;
    public string FileName { get; } = fileName;

    public static async Task<QQMediaSource> ResolveAsync(ResourceSegment resource)
    {
        if (resource.Uri.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            // Slice the existing string without copying the (potentially very large) payload.
            var offset = resource.Uri.AsSpan(7).StartsWith("//", StringComparison.Ordinal) ? 9 : 7;
            var encoded = resource.Uri.AsMemory(offset);
            var fileName = resource.FileName ?? resource switch
            {
                ImageSegment => "image.png",
                VideoSegment => "video.mp4",
                AudioSegment => "audio.silk",
                _ => "file.bin"
            };
            var temporaryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shirobot-qq-{Guid.NewGuid():N}.upload");
            try
            {
                await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await WriteBase64Async(encoded, output).ConfigureAwait(false);
                return new QQMediaSource(temporaryPath, null, fileName, temporary: true);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }

        Uri.TryCreate(resource.Uri, UriKind.Absolute, out var sourceUri);
        var url = sourceUri?.Scheme is "http" or "https" ? sourceUri : null;
        var path = System.IO.Path.IsPathFullyQualified(resource.Uri) ? resource.Uri
            : sourceUri?.Scheme == Uri.UriSchemeFile ? sourceUri.LocalPath
            : sourceUri is null ? System.IO.Path.GetFullPath(resource.Uri) : null;
        if (url is null && path is null)
            throw new NotSupportedException("QQ Official media source must be an HTTP(S) URL, a local file path or Base64.");
        return new QQMediaSource(path, url, resource.FileName ?? (path is not null
            ? System.IO.Path.GetFileName(path)
            : Uri.UnescapeDataString(System.IO.Path.GetFileName(url!.AbsolutePath))));
    }

    internal static async Task WriteBase64Async(ReadOnlyMemory<char> encoded, Stream output,
        long maxFileSize = MaxFileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSize);
        // Reject oversized inputs before decoding or writing. Only Base64's ASCII whitespace is allowed.
        long symbols = 0;
        var padding = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            var character = encoded.Span[index];
            if (IsBase64Whitespace(character)) continue;
            symbols++;
            padding = character == '=' ? padding + 1 : 0;
            if (symbols / 4 * 3 > maxFileSize + 2) throw InvalidSize();
        }
        if (symbols == 0) throw InvalidSize();
        if (symbols % 4 != 0 || padding > 2) throw InvalidBase64();
        if (symbols / 4 * 3 - padding > maxFileSize) throw InvalidSize();

        // Each send uses fixed-size buffers, independent of the file size.
        var characters = new char[16 * 1024];
        var bytes = new byte[12 * 1024];
        var count = 0;
        long written = 0;
        var finished = false;
        for (var index = 0; index < encoded.Length; index++)
        {
            var character = encoded.Span[index];
            if (IsBase64Whitespace(character)) continue;
            if (finished) throw InvalidBase64();
            characters[count++] = character;
            if (count != characters.Length && index != encoded.Length - 1) continue;
            await FlushAsync().ConfigureAwait(false);
        }
        if (count > 0) await FlushAsync().ConfigureAwait(false);

        async Task FlushAsync()
        {
            if (!Convert.TryFromBase64Chars(characters.AsSpan(0, count), bytes, out var decoded))
                throw InvalidBase64();
            if (decoded > maxFileSize - written) throw InvalidSize();
            finished = characters[count - 1] == '=';
            await output.WriteAsync(bytes.AsMemory(0, decoded)).ConfigureAwait(false);
            written += decoded;
            count = 0;
        }
    }

    private static bool IsBase64Whitespace(char character) => character is ' ' or '\t' or '\r' or '\n';

    private static NotSupportedException InvalidSize() =>
        new("QQ Official files must be between 1 byte and 200 MB.");

    private static ArgumentException InvalidBase64() =>
        new("QQ Official media source contains invalid Base64.", "resource");

    public void Dispose()
    {
        if (temporary) File.Delete(Path!);
    }
}
