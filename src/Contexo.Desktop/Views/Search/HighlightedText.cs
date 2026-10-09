using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using Contexo.App.Search;

namespace Contexo.Desktop.Views.Search;

/// <summary>
/// Attached property that fills a <see cref="TextBlock"/> from a list of <see cref="TextFragment"/>:
/// highlighted fragments get the warm "Brush.WarnSoft" background. The brush is looked up again when the theme changes.
/// </summary>
public static class HighlightedText
{
    public static readonly AttachedProperty<IReadOnlyList<TextFragment>?> FragmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<TextFragment>?>("Fragments", typeof(HighlightedText));

    static HighlightedText()
    {
        FragmentsProperty.Changed.AddClassHandler<TextBlock>(OnFragmentsChanged);
    }

    public static IReadOnlyList<TextFragment>? GetFragments(TextBlock element) => element.GetValue(FragmentsProperty);

    public static void SetFragments(TextBlock element, IReadOnlyList<TextFragment>? value) => element.SetValue(FragmentsProperty, value);

    private static void OnFragmentsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs args)
    {
        textBlock.ActualThemeVariantChanged -= OnThemeChanged;
        textBlock.ActualThemeVariantChanged += OnThemeChanged;
        textBlock.AttachedToVisualTree -= OnAttached;
        textBlock.AttachedToVisualTree += OnAttached;
        Rebuild(textBlock);
    }

    private static void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            Rebuild(textBlock);
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            Rebuild(textBlock);
        }
    }

    private static void Rebuild(TextBlock textBlock)
    {
        var fragments = GetFragments(textBlock);
        textBlock.Inlines ??= new InlineCollection();
        textBlock.Inlines.Clear();
        if (fragments is null)
        {
            return;
        }

        IBrush? highlight = null;
        if (fragments.Any(f => f.Highlight)
            && textBlock.TryFindResource("Brush.WarnSoft", textBlock.ActualThemeVariant, out var resource))
        {
            highlight = resource as IBrush;
        }

        foreach (var fragment in fragments)
        {
            var run = new Run(fragment.Text);
            if (fragment.Highlight)
            {
                run.Background = highlight ?? Brushes.Gold;
                run.FontWeight = FontWeight.SemiBold;
            }

            textBlock.Inlines.Add(run);
        }
    }
}
