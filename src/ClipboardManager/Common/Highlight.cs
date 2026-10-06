using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClipboardManager.Common;

/// <summary>Attached properties that render search matches in bold (shape, not only colour, for accessibility).</summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
        "Terms", typeof(object), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static object? GetTerms(DependencyObject element) => element.GetValue(TermsProperty);

    public static void SetTerms(DependencyObject element, object? value) => element.SetValue(TermsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = GetText(block) ?? string.Empty;
        var terms = GetTerms(block) as string[];
        block.Inlines.Clear();
        if (terms is not { Length: > 0 } || text.Length == 0)
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var position = 0;
        while (position < text.Length)
        {
            var (index, length) = NextMatch(text, position, terms);
            if (index < 0)
            {
                block.Inlines.Add(new Run(text[position..]));
                break;
            }

            if (index > position)
            {
                block.Inlines.Add(new Run(text[position..index]));
            }

            block.Inlines.Add(new Run(text.Substring(index, length)) { FontWeight = FontWeights.Bold, TextDecorations = TextDecorations.Underline });
            position = index + length;
        }
    }

    private static (int Index, int Length) NextMatch(string text, int start, string[] terms)
    {
        var best = (-1, 0);
        foreach (var term in terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            var index = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best.Item1 < 0 || index < best.Item1))
            {
                best = (index, term.Length);
            }
        }

        return best;
    }
}
