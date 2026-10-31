namespace NewsDesk.Core.Models;

/// <summary>
/// The headlines segment written as plain text, one headline per line:
///   HEADLINE | caption
/// A tab also separates the two (two columns pasted from Excel or Word).
/// Blank lines are ignored; a line without a separator has no caption.
/// </summary>
public static class HeadlineListText
{
    public const string Separator = " | ";

    public static List<(string Title, string Caption)> Parse(string? text)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Optional list numbering the operator may have typed or pasted ("1." / "2)")
            int digits = 0;
            while (digits < line.Length && char.IsDigit(line[digits])) digits++;
            if (digits is > 0 and <= 2 && digits < line.Length && line[digits] is '.' or ')')
                line = line[(digits + 1)..].TrimStart();

            int split = line.IndexOf('\t');
            if (split < 0) split = line.IndexOf('|');

            string title = split < 0 ? line : line[..split].Trim();
            string caption = split < 0 ? string.Empty : line[(split + 1)..].Trim().Trim('|', '\t').Trim();
            if (title.Length == 0) continue;
            result.Add((title, caption));
        }
        return result;
    }

    public static string Format(IEnumerable<HeadlineSequenceItem> items) =>
        string.Join(Environment.NewLine, items.Select(i =>
            string.IsNullOrWhiteSpace(i.Subtitle) ? i.Title : i.Title + Separator + i.Subtitle));
}
