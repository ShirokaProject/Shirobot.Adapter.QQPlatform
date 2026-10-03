using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQChunkedUploadClient(QQApiTransport transport, HttpClient uploadHttp)
{
    private const long MaxFileSize = 200L * 1024 * 1024;
    private const int FirstHashSize = 10_002_432;

    public async Task<QQUploadResponse> UploadAsync(Channel channel, int fileType, string path,
        string fileName, bool sendMessage, CancellationToken cancellationToken)
    {
        if (fileType is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(fileType));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        fileName = Path.GetFileName(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 or > MaxFileSize)
            throw new NotSupportedException("QQ Official files must be between 1 byte and 200 MB.");

        var hashes = await ComputeHashesAsync(file, cancellationToken).ConfigureAwait(false);
        using var prepareResponse = await transport.SendAsync(HttpMethod.Post, QQApiRoutes.UploadPrepare(channel),
            new QQUploadPrepareRequest(fileType, file.Length.ToString(CultureInfo.InvariantCulture),
                fileName, hashes.Md5, hashes.Sha1, hashes.Md5First10M), cancellationToken).ConfigureAwait(false);
        var prepared = await prepareResponse.Content.ReadFromJsonAsync<QQUploadPrepareResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prepared?.UploadId) || prepared.BlockSize <= 0 || prepared.Parts is null)
            throw new InvalidDataException("QQ upload_prepare response is incomplete.");

        var expectedParts = (file.Length + prepared.BlockSize - 1) / prepared.BlockSize;
        if (prepared.Parts.Count != expectedParts)
            throw new InvalidDataException("QQ upload_prepare returned an unexpected number of parts.");
        var parts = prepared.Parts.OrderBy(part => part.Index).ToArray();
        var firstPartIndex = parts[0].Index;
        if (firstPartIndex is not (0 or 1))
            throw new InvalidDataException("QQ upload_prepare returned an unsupported part index base.");
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Index != firstPartIndex + index
                || !Uri.TryCreate(parts[index].PresignedUrl, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("QQ upload_prepare returned an invalid part or URL.");
        }

        foreach (var part in parts)
        {
            var offset = (long)(part.Index - firstPartIndex) * prepared.BlockSize;
            var length = checked((int)Math.Min(prepared.BlockSize, file.Length - offset));
            var bytes = new byte[length];
            file.Position = offset;
            await file.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await PutPartAsync(new Uri(part.PresignedUrl!), bytes, cancellationToken).ConfigureAwait(false);
            var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
            await FinishPartAsync(channel, new QQUploadPartFinishRequest(prepared.UploadId, part.Index,
                length.ToString(CultureInfo.InvariantCulture), md5), prepared.Config, cancellationToken).ConfigureAwait(false);
        }

        using var completeResponse = await transport.SendAsync(HttpMethod.Post, QQApiRoutes.Files(channel),
            new QQUploadCompleteRequest(fileType, sendMessage, fileName, prepared.UploadId),
            cancellationToken).ConfigureAwait(false);
        return await completeResponse.Content.ReadFromJsonAsync<QQUploadResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ file upload completion response is empty.");
    }

    /// <summary>QQ answers 40093001 when its storage channel hiccups; the documented remedy is to retry for a while.</summary>
    private async Task FinishPartAsync(Channel channel, QQUploadPartFinishRequest request,
        QQUploadConfig? config, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config?.RetryTimeoutSeconds ?? 120, 1, 600));
        var delay = TimeSpan.FromSeconds(Math.Clamp(config?.RetryDelaySeconds ?? 1, 1, 10));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await transport.SendAsync(HttpMethod.Post, QQApiRoutes.UploadPartFinish(channel),
                    request, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (QQApiException error) when (error.ErrorCode == 40093001 && DateTimeOffset.UtcNow + delay < deadline)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PutPartAsync(Uri url, byte[] bytes, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new ByteArrayContent(bytes)
                };
                using var response = await uploadHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
                if ((int)response.StatusCode < 500 || attempt == 2)
                    throw new HttpRequestException($"QQ file part upload failed: HTTP {(int)response.StatusCode}.",
                        null, response.StatusCode);
            }
            catch (HttpRequestException error) when (attempt < 2 &&
                (error.StatusCode is null || (int)error.StatusCode >= 500))
            {
                // A new signed PUT request can safely retry the same part.
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(string Md5, string Sha1, string Md5First10M)> ComputeHashesAsync(
        FileStream file, CancellationToken cancellationToken)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var first = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var bytes = new byte[128 * 1024];
        var firstRemaining = FirstHashSize;
        int count;
        while ((count = await file.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            md5.AppendData(bytes, 0, count);
            sha1.AppendData(bytes, 0, count);
            var firstCount = Math.Min(count, firstRemaining);
            if (firstCount > 0)
            {
                first.AppendData(bytes, 0, firstCount);
                firstRemaining -= firstCount;
            }
        }
        file.Position = 0;
        return (Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(first.GetHashAndReset()));
    }
}
