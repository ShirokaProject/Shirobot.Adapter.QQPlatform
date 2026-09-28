using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.QQPlatform.Contracts;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQKeyboardMapper
{
    public static QQKeyboardWire Map(QQKeyboard keyboard)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        if (keyboard.Rows is not { Count: > 0 and <= 5 })
            throw new ArgumentException("QQ keyboard must contain 1 to 5 rows.", nameof(keyboard));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<QQKeyboardRowWire>(keyboard.Rows.Count);
        foreach (var row in keyboard.Rows)
        {
            if (row?.Buttons is not { Count: > 0 and <= 5 })
                throw new ArgumentException("Each QQ keyboard row must contain 1 to 5 buttons.", nameof(keyboard));
            var buttons = new List<QQButtonWire>(row.Buttons.Count);
            foreach (var button in row.Buttons)
            {
                if (button is null || string.IsNullOrWhiteSpace(button.Id) || !ids.Add(button.Id)
                    || string.IsNullOrWhiteSpace(button.Label) || string.IsNullOrWhiteSpace(button.Data))
                    throw new ArgumentException("QQ keyboard buttons need unique IDs, labels and action data.", nameof(keyboard));
                if (!Enum.IsDefined(button.ActionType) || !Enum.IsDefined(button.Style) || !Enum.IsDefined(button.Permission))
                    throw new ArgumentException("QQ keyboard contains an unsupported enum value.", nameof(keyboard));
                if (button.ActionType == QQButtonActionType.OpenUrl &&
                    (!Uri.TryCreate(button.Data, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
                    throw new ArgumentException("QQ URL buttons require an HTTP(S) URL.", nameof(keyboard));
                if (button.Permission == QQButtonPermissionType.SpecificUsers &&
                    (button.SpecificUserIds is not { Count: > 0 } || button.SpecificUserIds.Any(string.IsNullOrWhiteSpace)))
                    throw new ArgumentException("Specific-user buttons require user OpenIDs.", nameof(keyboard));
                if (button.Permission != QQButtonPermissionType.SpecificUsers && button.SpecificUserIds is { Count: > 0 })
                    throw new ArgumentException("Only specific-user buttons may set user OpenIDs.", nameof(keyboard));

                buttons.Add(new QQButtonWire(button.Id,
                    new QQButtonRenderData(button.Label, button.VisitedLabel ?? button.Label, (int)button.Style),
                    new QQButtonAction
                    {
                        Type = (int)button.ActionType,
                        Permission = new QQButtonPermission
                        {
                            Type = (int)button.Permission,
                            SpecificUserIds = button.SpecificUserIds
                        },
                        Data = button.Data,
                        Enter = button.ActionType == QQButtonActionType.SendCommand ? button.Enter : null,
                        Reply = button.ActionType == QQButtonActionType.SendCommand ? button.Reply : null
                    }));
            }
            rows.Add(new QQKeyboardRowWire(buttons));
        }
        return new QQKeyboardWire(new QQKeyboardContent(rows));
    }
}
