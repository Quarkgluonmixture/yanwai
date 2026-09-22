namespace Yanwai.Overlay;

/// <summary>
/// What the full HUD card shows: a headline verdict, a suggested next step, and a few
/// rows. Long lists belong in the panel, not here.
/// </summary>
public sealed record OverlayContent(
    string Message,
    string Headline,
    string Advice,
    IReadOnlyList<OverlayRow> Rows);

public sealed record OverlayRow(string Label, double Probability)
{
    public string ProbabilityText => $"{Probability * 100:N0}%";
}

/// <summary>
/// What the one-line chip shows. <paramref name="Severity"/> is the danger level
/// index, or null when no danger answer came back — which must not render as "safe".
/// </summary>
public sealed record OverlayChip(string Text, int? Severity);
