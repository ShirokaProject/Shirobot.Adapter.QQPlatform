namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>Splits text that is too long for one QQ message, preferring line breaks.</summary>
internal static class QQTextChunker
{
    /// <summary>The reference QQ SDK caps one text message at about 5000 characters.</summary>
    public const int MaxLength = 5000;

    public static IReadOnlyList<string> Split(string text, int limit)
    {
        if (text.Length <= limit) return [text];
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= limit)
            {
                chunks.Add(text[start..]);
                break;
            }
            var end = start + limit;
            var lineBreak = text.LastIndexOf('\n', end - 1, limit / 5);
            if (lineBreak > start) end = lineBreak + 1;
            else if (char.IsHighSurrogate(text[end - 1])) end--;
            chunks.Add(text[start..end]);
            start = end;
        }
        return chunks;
    }
}
