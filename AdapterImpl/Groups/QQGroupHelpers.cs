using System.Globalization;
using System.Text.Json.Nodes;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;

/// <summary>Shared route, pagination and JSON-body rules for QQ group management endpoints.</summary>
internal static class QQGroupHelpers
{
    public static string Id(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }

    public static string GroupRoute(string id, string resource) => $"v2/groups/{Id(id)}/{resource}";

    public static string StrategyRoute(string id) => $"v2/groups/join_approval_strategy/{Id(id)}";

    public static string Page(string route, string? cursor, int limit)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        return $"{route}?limit={limit}" +
            (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
    }

    public static string Stamp(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);

    public static JsonArray Strings(IReadOnlyList<string> values, int max)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is 0 || values.Count > max || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Expected 1 to {max} non-empty identifiers.", nameof(values));
        return new JsonArray(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }

    public static void Groups(JsonObject body, IReadOnlyList<string>? openIds, IReadOnlyList<string>? ids)
    {
        if ((openIds is null) == (ids is null))
            throw new ArgumentException("Specify either group OpenIDs or group numbers.");
        if (openIds is not null)
        {
            body["group_openids"] = Strings(openIds, 100);
            return;
        }
        if (ids!.Count is 0 or > 100 || ids.Any(x => !ulong.TryParse(x, NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) || number == 0))
            throw new ArgumentException("Expected 1 to 100 group numbers.");
        body["group_ids"] = new JsonArray(ids
            .Select(x => (JsonNode?)JsonValue.Create(ulong.Parse(x, CultureInfo.InvariantCulture))).ToArray());
    }

    public static void Remark(JsonObject body, string? remark)
    {
        if (remark is null) return;
        if (remark.EnumerateRunes().Count() > 255)
            throw new ArgumentException("Strategy remark exceeds 255 characters.", nameof(remark));
        body["remark"] = remark;
    }
}
