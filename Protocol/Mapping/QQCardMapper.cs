using System.Text;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol.Mapping;

internal static class QQCardMapper
{
    internal static QQCard Map(QOfficialCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        ValidateText(card.Title, 50, nameof(card.Title));
        ValidateText(card.Description, 200, nameof(card.Description));
        ValidateUrl(card.Url, nameof(card.Url));
        ValidateUrl(card.PictureUrl, nameof(card.PictureUrl));
        return new("tuwen", new(card.Title, card.Description, card.Url, card.PictureUrl));
    }

    internal static QQCard Map(CardSegment card)
    {
        if (card.Fields.Count != 0) throw new NotSupportedException("QQ image-text cards cannot contain extra fields.");
        return Map(new QOfficialCard(card.Title!, card.Description!, card.Url!, card.ImageUrl!));
    }

    private static void ValidateText(string text, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > maxLength)
            throw new ArgumentException($"QQ card {name} requires 1-{maxLength} characters.", name);
    }

    private static void ValidateUrl(string url, string name)
    {
        if (url is null || url.Length > 1024 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"QQ card {name} requires an HTTPS URL of at most 1024 characters.", name);
    }
}
