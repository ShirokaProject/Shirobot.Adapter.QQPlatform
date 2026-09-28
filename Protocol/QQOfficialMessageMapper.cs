using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Maps shared QQ official models to the OpenAPI wire format.</summary>
internal static class QQOfficialMessageMapper
{
    public static QQMarkdown MapMarkdown(QOfficialMarkdown markdown) => markdown switch
    {
        QCustomMarkdown custom when !string.IsNullOrWhiteSpace(custom.Content) =>
            new QQMarkdown { Content = custom.Content },
        QTemplateMarkdown template when !string.IsNullOrWhiteSpace(template.CustomTemplateId)
            && template.Params is not null && template.Params.All(ValidParameter) =>
            new QQMarkdown
            {
                CustomTemplateId = template.CustomTemplateId,
                Params = template.Params.Select(p => new QQMarkdownParamWire(p.Key, p.Values)).ToArray()
            },
        _ => throw new ArgumentException("QQ official Markdown content or template is invalid.", nameof(markdown))
    };

    public static QQKeyboardWire? MapKeyboard(QOfficialKeyboard? keyboard) => keyboard switch
    {
        null => null,
        QKeyboardTemplate template when !string.IsNullOrWhiteSpace(template.Id) =>
            new QQKeyboardWire { Id = template.Id },
        QInlineKeyboard inline => MapInline(inline),
        _ => throw new ArgumentException("QQ official keyboard is invalid.", nameof(keyboard))
    };

    private static bool ValidParameter(QMarkdownParameter parameter) =>
        parameter is not null && !string.IsNullOrWhiteSpace(parameter.Key)
        && parameter.Values is not null && parameter.Values.Count > 0
        && parameter.Values.All(value => !string.IsNullOrWhiteSpace(value));

    private static QQKeyboardWire MapInline(QInlineKeyboard keyboard)
    {
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
                if (button is null || button.RenderData is null || button.Action is null
                    || string.IsNullOrWhiteSpace(button.RenderData.Label)
                    || string.IsNullOrWhiteSpace(button.RenderData.VisitedLabel)
                    || string.IsNullOrWhiteSpace(button.Action.Data)
                    || button.Id is { Length: > 0 } id && !ids.Add(id)
                    || button.Id is { Length: 0 })
                    throw new ArgumentException("QQ keyboard button fields or IDs are invalid.", nameof(keyboard));
                var action = button.Action;
                var permission = action.Permission;
                if (permission is null || !Enum.IsDefined(button.RenderData.Style)
                    || !Enum.IsDefined(action.Type) || !Enum.IsDefined(permission.Type)
                    || permission.Type == QKeyboardPermissionType.SpecifiedRoles)
                    throw new ArgumentException("QQ keyboard action or permission is not supported for group and direct messages.", nameof(keyboard));
                if (action.Type == QKeyboardActionType.Jump
                    && (!Uri.TryCreate(action.Data, UriKind.Absolute, out var url)
                        || url.Scheme is not ("http" or "https")))
                    throw new ArgumentException("QQ URL buttons require an HTTP(S) URL.", nameof(keyboard));
                if (permission.Type == QKeyboardPermissionType.SpecifiedUsers
                    && (permission.SpecifyUserIds is not { Count: > 0 }
                        || permission.SpecifyUserIds.Any(string.IsNullOrWhiteSpace)))
                    throw new ArgumentException("Specified-user buttons require user OpenIDs.", nameof(keyboard));
                if (permission.Type != QKeyboardPermissionType.SpecifiedUsers
                    && permission.SpecifyUserIds is { Count: > 0 })
                    throw new ArgumentException("Only specified-user buttons may set user OpenIDs.", nameof(keyboard));
                if (action.Anchor is < 0)
                    throw new ArgumentException("QQ command button anchor cannot be negative.", nameof(keyboard));
                buttons.Add(new QQButtonWire(button.Id,
                    new QQButtonRenderData(button.RenderData.Label, button.RenderData.VisitedLabel,
                        (int)button.RenderData.Style),
                    new QQButtonAction
                    {
                        Type = (int)action.Type,
                        Permission = new QQButtonPermission
                        {
                            Type = (int)permission.Type,
                            SpecificUserIds = permission.SpecifyUserIds
                        },
                        Data = action.Data,
                        UnsupportedTips = action.UnsupportTips,
                        Reply = action.Type == QKeyboardActionType.Command ? action.Reply : null,
                        Enter = action.Type == QKeyboardActionType.Command && action.Anchor is null ? action.Enter : null,
                        Anchor = action.Type == QKeyboardActionType.Command ? action.Anchor : null
                    }));
            }
            rows.Add(new QQKeyboardRowWire(buttons));
        }
        return new QQKeyboardWire { Content = new QQKeyboardContent(rows) };
    }
}
