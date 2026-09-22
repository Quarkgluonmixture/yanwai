namespace Yanwai.Overlay;

/// <summary>
/// What a message's HUD card shows: one section per question, each a heading and the
/// top options with their probabilities. Laid out as plain lines ("- 是：7%") so it reads
/// like a note written under the message rather than a dashboard.
/// </summary>
public sealed record OverlayContent(IReadOnlyList<OverlaySection> Sections);

/// <summary>A question. <paramref name="Rows"/> may be empty for a one-line answer.</summary>
public sealed record OverlaySection(string Heading, IReadOnlyList<OverlayRow> Rows);

public sealed record OverlayRow(string Label, double Probability)
{
    public string Line => $"- {Label}：{Probability * 100:N0}%";
}

/// <summary>
/// What the one-line chip shows when a card does not fit. <paramref name="Severity"/> is
/// the danger level index, or null when no danger answer came back — which must not
/// render as "safe".
/// </summary>
public sealed record OverlayChip(string Text, int? Severity);
