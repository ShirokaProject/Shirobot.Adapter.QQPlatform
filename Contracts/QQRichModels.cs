using ShiroBot.SDK.Models;

namespace ShiroBot.QQPlatform.Contracts;

public sealed record QQMarkdownMessage(string Content, QQKeyboard? Keyboard = null);

public sealed record QQKeyboard(IReadOnlyList<QQKeyboardRow> Rows);

public sealed record QQKeyboardRow(IReadOnlyList<QQButton> Buttons);

public enum QQButtonActionType { OpenUrl = 0, Callback = 1, SendCommand = 2 }
public enum QQButtonStyle { Secondary = 0, Primary = 1 }
public enum QQButtonPermissionType { SpecificUsers = 0, Administrators = 1, Everyone = 2 }

public sealed record QQButton(
    string Id,
    string Label,
    QQButtonActionType ActionType,
    string Data,
    QQButtonStyle Style = QQButtonStyle.Primary,
    QQButtonPermissionType Permission = QQButtonPermissionType.Everyone,
    IReadOnlyList<string>? SpecificUserIds = null,
    string? VisitedLabel = null,
    bool Enter = false,
    bool Reply = false);

public sealed record QQResponseReference(string? MessageId, string? EventId)
{
    public static QQResponseReference ForMessage(string messageId) => new(messageId, null);
    public static QQResponseReference ForEvent(string eventId) => new(null, eventId);
}

public sealed record QQInteractionEvent : BotEvent
{
    public required string InteractionId { get; init; }
    public required string EventId { get; init; }
    public required Channel Channel { get; init; }
    public required string UserId { get; init; }
    public required string ButtonData { get; init; }
    public string? ButtonId { get; init; }
    public string? MessageId { get; init; }
}
