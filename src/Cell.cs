using System.Text;

namespace UdpPingGraph;

/// <summary>
/// One character cell of the graph grid. <see cref="Background"/>, when
/// set, is a Spectre.Console color name ("red" / "green") used to shade
/// the tick's max marker per the drop/pending rules.
/// </summary>
internal readonly struct Cell(char character, string? background, string? foreground)
{
    public static readonly Cell Empty = new(' ', null, null);

    public char Character { get; } = character;
    public string? Background { get; } = background;
    public string? Foreground { get; } = foreground;

    /// <summary>
    /// Writes <paramref name="ch"/> into a cell that may already hold a
    /// marker from another statistic (min/median/max landing on the same
    /// row). Per spec, any such overlap always renders as '-', and a
    /// background color - once set - is preserved rather than cleared by
    /// a later write that has no color of its own.
    /// </summary>
    public static Cell Combine(Cell existing, char ch, string? background, string? foreground)
    {
        var resolvedChar = existing.Character == ' ' ? ch : existing.Character;
        var resolvedBackground = background ?? existing.Background;
        var resolvedForeground = existing.Foreground ?? foreground;
        return new Cell(resolvedChar, resolvedBackground, resolvedForeground);
    }

    /// <summary>
    /// Appends this cell's Spectre.Console markup directly to
    /// <paramref name="sb"/>, avoiding a per-cell string allocation while
    /// building a full row of the graph.
    /// </summary>
    public void AppendMarkup(StringBuilder sb)
    {
        switch (Background, Foreground)
        {
            case (null, null):
                sb.Append(Character);
                break;
            case (not null, null):
                sb.Append("[on ").Append(Background).Append(']').Append(Character).Append("[/]");
                break;
            case (null, not null):
                sb.Append('[').Append(Foreground).Append(']').Append(Character).Append("[/]");
                break;
            case (not null, not null):
                sb.Append('[').Append(Foreground).Append(" on ").Append(Background).Append(']').Append(Character).Append("[/]");
                break;
        }
    }
}
