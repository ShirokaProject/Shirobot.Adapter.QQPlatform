using ShiroBot.SDK.Models;

namespace ShiroBot.QQPlatform.Contracts;

/// <summary>QQ 官方平台专属的发送能力。通过 IBotContext.GetAdapterExtension 获取。</summary>
public interface IQQPlatformMessageService
{
    Task<SentMessage> SendMarkdownAsync(Channel channel, string markdown, string replyToMessageId);
    Task<SentMessage> SendTextAsync(Channel channel, string text, QQResponseReference response);
    Task SendTypingAsync(Channel channel, string replyToMessageId, TimeSpan duration);
    IQQPlatformMessageStream BeginStream(Channel channel, string replyToMessageId, QQStreamContentType contentType = QQStreamContentType.Text);
    Task<SentMessage> SendRichMarkdownAsync(Channel channel, QQMarkdownMessage message, QQResponseReference response);
    Task<SentMessage> SendArkAsync(Channel channel, int templateId, IReadOnlyDictionary<string, string> fields, string replyToMessageId);
}

public enum QQStreamContentType { Text, Markdown }

/// <summary>AppendAsync receives the full visible text. Earlier text must remain an exact prefix.</summary>
public interface IQQPlatformMessageStream : IAsyncDisposable
{
    bool HasStarted { get; }
    Task AppendAsync(string cumulativeText, CancellationToken cancellationToken = default);
    Task<SentMessage> CompleteAsync(CancellationToken cancellationToken = default);
}
