using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.QQPlatform.Contracts;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQPlatformMessageStream(
    QQOpenApiClient api, Channel channel, string replyToMessageId, int messageSequence,
    QQStreamContentType contentType, IConsoleLogger? logger) : IQQPlatformMessageStream
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _streamMessageId;
    private string _text = string.Empty;
    private int _index;
    private DateTimeOffset _lastFrameAt;
    private bool _completed;
    private bool _failed;
    private bool _disposed;

    public bool HasStarted => _streamMessageId is not null;

    public async Task AppendAsync(string cumulativeText, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cumulativeText);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!cumulativeText.StartsWith(_text, StringComparison.Ordinal))
                throw new ArgumentException("Stream updates must retain the previous text as an exact prefix.", nameof(cumulativeText));
            if (cumulativeText == _text) return;
            await SendFrameAsync(cumulativeText, 1, cancellationToken).ConfigureAwait(false);
            _text = cumulativeText;
        }
        finally { _gate.Release(); }
    }

    public async Task<SentMessage> CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!HasStarted) throw new InvalidOperationException("Append text before completing the stream.");
            await SendFrameAsync(_text, 10, cancellationToken).ConfigureAwait(false);
            _completed = true;
            logger?.Info($"已完成私聊流式消息到 {channel.Id}: {_streamMessageId}");
            return new SentMessage(_streamMessageId!);
        }
        finally { _gate.Release(); }
    }

    private async Task SendFrameAsync(string text, int state, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(500) - (DateTimeOffset.UtcNow - _lastFrameAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        var frame = new QQStreamRequest
        {
            InputState = state,
            ContentType = contentType == QQStreamContentType.Markdown ? "markdown" : "text",
            Content = text,
            MessageId = replyToMessageId,
            EventId = replyToMessageId,
            MessageSequence = messageSequence,
            Index = _index,
            StreamMessageId = _streamMessageId
        };
        try
        {
            var id = await api.SendStreamFrameAsync(channel, frame, cancellationToken).ConfigureAwait(false);
            if (_streamMessageId is null)
                _streamMessageId = !string.IsNullOrWhiteSpace(id) ? id
                    : throw new InvalidDataException("QQ stream response contains no stream message ID.");
            _index++;
            _lastFrameAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            // A failed HTTP exchange may already have reached QQ; reusing its index could duplicate a frame.
            _failed = true;
            throw;
        }
    }

    private void EnsureOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QQPlatformMessageStream));
        if (_completed) throw new InvalidOperationException("The stream is already complete.");
        if (_failed) throw new InvalidOperationException("The stream failed and cannot safely continue.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _gate.Release(); }
    }
}
