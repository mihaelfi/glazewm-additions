using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GlazeLauncher;

public readonly record struct HighlightedText(string Text, int[]? Positions, int Offset = 0);

public sealed class HighlightedTextBlock : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(HighlightedText), typeof(HighlightedTextBlock),
        new PropertyMetadata(new HighlightedText("", null), OnValueChanged));

    public HighlightedText Value
    {
        get => (HighlightedText)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        ((HighlightedTextBlock)element).RenderText((HighlightedText)e.NewValue);

    private void RenderText(HighlightedText value)
    {
        Inlines.Clear();
        if (value.Positions is not { Length: > 0 } positions)
        {
            Text = value.Text;
            return;
        }

        // Keep combining marks and surrogate pairs in the same run as their base character.
        var text = value.Text;
        int match = 0;
        int runStart = 0;
        bool highlighted = false;
        for (int index = 0; index < text.Length;)
        {
            int end = index + StringInfo.GetNextTextElementLength(text.AsSpan(index));
            bool matches = match < positions.Length && positions[match] + value.Offset < end;
            if (matches != highlighted)
            {
                AddRun(text[runStart..index], highlighted);
                runStart = index;
                highlighted = matches;
            }

            while (match < positions.Length && positions[match] + value.Offset < end) match++;
            index = end;
        }
        AddRun(text[runStart..], highlighted);
    }

    private void AddRun(string text, bool highlighted)
    {
        if (text.Length == 0) return;
        var run = new Run(text);
        if (highlighted)
        {
            run.FontWeight = FontWeights.SemiBold;
            run.SetResourceReference(TextElement.ForegroundProperty, "MatchBrush");
            run.SetResourceReference(TextElement.BackgroundProperty, "MatchBackgroundBrush");
        }
        Inlines.Add(run);
    }
}
