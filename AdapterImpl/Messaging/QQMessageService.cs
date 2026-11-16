using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>
///     SDK-facing message service. Incoming context, outbound encoding and passive reply sequencing are
///     separate collaborators; this type only keeps the public contract stable.
/// </summary>
internal sealed class QQMessageService : IMessageService, IDisposable
{
    private readonly QQIncomingMessageContext _context;
    private readonly QQPassiveReplyWindow _window;
    private readonly QQOutboundMessagePipeline _outbound;

    public QQMessageService(
        QQOpenApiClient api,
        IConsoleLogger? logger = null,
        Func<User?>? getSelfUser = null,
        TimeSpan? contextRetention = null,
        int contextMaxMessages = 4096)
    {
        _context = new QQIncomingMessageContext(contextRetention, contextMaxMessages, logger);
        _window = new QQPassiveReplyWindow();
        _outbound = new QQOutboundMessagePipeline(api, _context, _window, logger, getSelfUser);
    }

    public MessageCapabilities GetMessageCapabilities(Channel channel) => _outbound.GetCapabilities(channel);

    public MessageSendAssessment AssessMessage(Channel channel, OutgoingMessage message) =>
        _outbound.Assess(channel, message);

    public Task<SentMessage> SendMessageAsync(Channel channel, OutgoingMessage message,
        CancellationToken cancellationToken = default) =>
        _outbound.SendAsync(channel, message, cancellationToken);

    public Task<SentMessage> SendMessageAsync(Channel channel, IReadOnlyList<MessageSegment> segments,
        CancellationToken cancellationToken = default) =>
        _outbound.SendAsync(channel, segments, cancellationToken);

    public Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default) =>
        _outbound.DeleteMessageAsync(channel, messageId, cancellationToken);

    public Task<MessageEvent?> GetMessageAsync(Channel channel, string messageId,
        CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<MessageEvent?>(cancellationToken)
            : Task.FromResult(_context.Find(channel, messageId));

    public Task SendTypingAsync(Channel channel, string replyToMessageId, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        _outbound.SendTypingAsync(channel, replyToMessageId, duration, cancellationToken);

    internal MessageEvent RegisterIncoming(MessageEvent message) => _context.RegisterIncoming(message);

    internal void RememberInteraction(InteractionEvent interaction) => _context.RememberInteraction(interaction);

    internal IQOfficialMessageStream BeginOfficialStream(Channel channel, string replyToMessageId,
        QOfficialStreamContentType contentType = QOfficialStreamContentType.Text) =>
        _outbound.BeginStream(channel, replyToMessageId, contentType);

    internal Task<SentMessage> SendOfficialAsync(Channel channel, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default) =>
        _outbound.SendOfficialAsync(channel, content, reply, description, cancellationToken);

    internal Task<SentMessage> SendOfficialAsync(QOfficialMessageTarget target, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default) =>
        _outbound.SendOfficialAsync(target, content, reply, description, cancellationToken);

    internal Task<SentMessage> SendOfficialAsync(string route, string targetName, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default) =>
        _outbound.SendOfficialAsync(route, targetName, content, reply, description,
            cancellationToken: cancellationToken);

    internal static JsonElement? AddResolvedReplyChain(object? raw, IReadOnlyList<QQQuotedMessage> chain) =>
        QQIncomingMessageContext.AddResolvedReplyChain(raw, chain);

    public void Dispose()
    {
        _window.Clear();
        _context.Dispose();
    }
}
