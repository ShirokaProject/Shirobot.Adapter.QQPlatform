using ShiroBot.SDK.Models;

namespace ShiroBot.QQPlatform.Contracts;

/// <summary>上游 IQOfficialMessageApi 暂未覆盖的 QQ 平台能力。通过 IBotContext.GetAdapterExtension 获取。</summary>
public interface IQQPlatformMessageService
{
    Task SendTypingAsync(Channel channel, string replyToMessageId, TimeSpan duration);
    IQQPlatformMessageStream BeginStream(Channel channel, string replyToMessageId, QQStreamContentType contentType = QQStreamContentType.Text);
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
