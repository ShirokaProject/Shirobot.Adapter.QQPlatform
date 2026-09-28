using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.Adapter.QQPlatform.Wire;

internal sealed record TokenRequest(
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int ExpiresIn,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record GatewayResponse([property: JsonPropertyName("url")] string Url);

internal sealed record GatewayPayload(
    [property: JsonPropertyName("op")] int Op,
    [property: JsonPropertyName("d")] JsonElement Data,
    [property: JsonPropertyName("s")] long? Sequence,
    [property: JsonPropertyName("t")] string? EventType,
    [property: JsonPropertyName("id")] string? Id);

internal sealed record HelloData([property: JsonPropertyName("heartbeat_interval")] int HeartbeatInterval);

internal sealed record ReadyData(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("user")] QQUser? User);

internal sealed record QQUser
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("user_openid")] public string? UserOpenId { get; init; }
    [JsonPropertyName("member_openid")] public string? MemberOpenId { get; init; }
    [JsonPropertyName("member_role")] public string? MemberRole { get; init; }
    [JsonPropertyName("union_openid")] public string? UnionOpenId { get; init; }
    [JsonPropertyName("bot")] public bool Bot { get; init; }
}

internal sealed record QQAttachment
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("filename")] public string? FileName { get; init; }
    [JsonPropertyName("content_type")] public string? ContentType { get; init; }
    [JsonPropertyName("voice_wav_url")] public string? VoiceWavUrl { get; init; }
}

internal sealed record QQIncomingMessage
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("author")] public QQUser? Author { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("group_openid")] public string? GroupOpenId { get; init; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
    [JsonPropertyName("attachments")] public IReadOnlyList<QQAttachment>? Attachments { get; init; }
    [JsonPropertyName("mentions")] public IReadOnlyList<QQUser>? Mentions { get; init; }
    [JsonPropertyName("message_reference")] public QQMessageReference? MessageReference { get; init; }
}

internal sealed record QQMessageReference([property: JsonPropertyName("message_id")] string? MessageId);

internal sealed record QQGroupInfo
{
    [JsonPropertyName("group_openid")] public string? GroupOpenId { get; init; }
    [JsonPropertyName("group_name")] public string? GroupName { get; init; }
}

internal sealed record QQInteractionData
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public int Type { get; init; }
    [JsonPropertyName("scene")] public string? Scene { get; init; }
    [JsonPropertyName("chat_type")] public int ChatType { get; init; }
    [JsonPropertyName("group_openid")] public string? GroupOpenId { get; init; }
    [JsonPropertyName("group_member_openid")] public string? GroupMemberOpenId { get; init; }
    [JsonPropertyName("user_openid")] public string? UserOpenId { get; init; }
    [JsonPropertyName("user_id")] public string? UserId { get; init; }
    [JsonPropertyName("channel_id")] public string? ChannelId { get; init; }
    [JsonPropertyName("guild_id")] public string? GuildId { get; init; }
    [JsonPropertyName("data")] public QQInteractionPayload? Data { get; init; }
}

internal sealed record QQInteractionPayload
{
    [JsonPropertyName("resolved")] public QQInteractionResolved? Resolved { get; init; }
}

internal sealed record QQInteractionResolved
{
    [JsonPropertyName("button_data")] public string? ButtonData { get; init; }
    [JsonPropertyName("button_id")] public string? ButtonId { get; init; }
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }
}

internal sealed record QQSendRequest
{
    [JsonPropertyName("msg_type")] public int MessageType { get; init; }
    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; init; }
    [JsonPropertyName("msg_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MessageId { get; init; }
    [JsonPropertyName("msg_seq"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MessageSequence { get; init; }
    [JsonPropertyName("event_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EventId { get; init; }
    [JsonPropertyName("media"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQMedia? Media { get; init; }
    [JsonPropertyName("markdown"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQMarkdown? Markdown { get; init; }
    [JsonPropertyName("keyboard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQKeyboardWire? Keyboard { get; init; }
    [JsonPropertyName("ark"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQArk? Ark { get; init; }
    [JsonPropertyName("input_notify"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQInputNotify? InputNotify { get; init; }
}

internal sealed record QQInputNotify(
    [property: JsonPropertyName("input_type")] int InputType,
    [property: JsonPropertyName("input_second")] int DurationSeconds);

internal sealed record QQStreamRequest
{
    [JsonPropertyName("input_mode")] public string InputMode { get; init; } = "replace";
    [JsonPropertyName("input_state")] public int InputState { get; init; }
    [JsonPropertyName("content_type")] public required string ContentType { get; init; }
    [JsonPropertyName("content_raw")] public required string Content { get; init; }
    [JsonPropertyName("msg_id")] public required string MessageId { get; init; }
    [JsonPropertyName("event_id")] public required string EventId { get; init; }
    [JsonPropertyName("msg_seq")] public int MessageSequence { get; init; }
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("stream_msg_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StreamMessageId { get; init; }
}

internal sealed record QQMedia([property: JsonPropertyName("file_info")] string FileInfo);
internal sealed record QQMarkdown
{
    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; init; }
    [JsonPropertyName("custom_template_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CustomTemplateId { get; init; }
    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<QQMarkdownParamWire>? Params { get; init; }
}
internal sealed record QQMarkdownParamWire(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("values")] IReadOnlyList<string> Values);
internal sealed record QQKeyboardWire
{
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; init; }
    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QQKeyboardContent? Content { get; init; }
}
internal sealed record QQKeyboardContent([property: JsonPropertyName("rows")] IReadOnlyList<QQKeyboardRowWire> Rows);
internal sealed record QQKeyboardRowWire([property: JsonPropertyName("buttons")] IReadOnlyList<QQButtonWire> Buttons);
internal sealed record QQButtonWire(
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonPropertyName("render_data")] QQButtonRenderData RenderData,
    [property: JsonPropertyName("action")] QQButtonAction Action);
internal sealed record QQButtonRenderData(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("visited_label")] string VisitedLabel,
    [property: JsonPropertyName("style")] int Style);
internal sealed record QQButtonAction
{
    [JsonPropertyName("type")] public int Type { get; init; }
    [JsonPropertyName("permission")] public required QQButtonPermission Permission { get; init; }
    [JsonPropertyName("data")] public required string Data { get; init; }
    [JsonPropertyName("unsupport_tips")] public string UnsupportedTips { get; init; } = "当前客户端不支持该操作";
    [JsonPropertyName("enter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Enter { get; init; }
    [JsonPropertyName("reply"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Reply { get; init; }
    [JsonPropertyName("anchor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Anchor { get; init; }
}
internal sealed record QQButtonPermission
{
    [JsonPropertyName("type")] public int Type { get; init; }
    [JsonPropertyName("specify_user_ids"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? SpecificUserIds { get; init; }
    [JsonPropertyName("specify_role_ids"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? SpecificRoleIds { get; init; }
}
internal sealed record QQArk(
    [property: JsonPropertyName("template_id")] int TemplateId,
    [property: JsonPropertyName("kv")] IReadOnlyList<QQArkField> Fields);
internal sealed record QQArkField(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);
internal sealed record QQSendResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("timestamp")] JsonElement Timestamp);

internal sealed record QQUploadRequest(
    [property: JsonPropertyName("file_type")] int FileType,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("srv_send_msg")] bool ServerSendMessage);
internal sealed record QQUploadResponse([property: JsonPropertyName("file_info")] string FileInfo);
