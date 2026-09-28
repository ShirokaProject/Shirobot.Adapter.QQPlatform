using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Converts QQ's inline content tags to SDK segments without changing ordinary text.</summary>
internal static partial class QQContentParser
{
    [GeneratedRegex("<@!?((?:[A-Za-z0-9_-]+)|all)>|<qqbot-at-user\\s+id=\"([^\"]+)\"\\s*/>|<faceType=([0-9]+),faceId=\"([^\"]+)\",ext=\"([^\"]*)\">",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineTagRegex();

    public static IReadOnlyList<MessageSegment> Parse(string? content, IReadOnlyDictionary<string, string>? mentionNames = null)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var segments = new List<MessageSegment>();
        var offset = 0;
        foreach (Match match in InlineTagRegex().Matches(content))
        {
            if (match.Index > offset) segments.Add(new TextSegment(content[offset..match.Index]));
            if (match.Groups[1].Success || match.Groups[2].Success)
            {
                var id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                segments.Add(id.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? new MentionAllSegment()
                    : new MentionSegment(id)
                    {
                        DisplayName = mentionNames is not null && mentionNames.TryGetValue(id, out var name) ? name : null
                    });
            }
            else
            {
                var id = match.Groups[4].Value;
                segments.Add(new EmojiSegment(id) { Name = DecodeFaceName(match.Groups[5].Value) });
            }
            offset = match.Index + match.Length;
        }
        if (offset < content.Length) segments.Add(new TextSegment(content[offset..]));
        return segments;
    }

    private static string? DecodeFaceName(string encoded)
    {
        if (encoded.Length is 0 or > 4096) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
            return json.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
