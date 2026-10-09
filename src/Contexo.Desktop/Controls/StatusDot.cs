using Avalonia;
using Avalonia.Controls.Shapes;
using Contexo.App.Shell;

namespace Contexo.Desktop.Controls;

/// <summary>Small coloured dot in front of a status bar item. The colour follows <see cref="Tone"/> and the current theme.</summary>
public sealed class StatusDot : Ellipse
{
    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusDot, StatusTone>(nameof(Tone));

    protected override Type StyleKeyOverride => typeof(Ellipse);

    public StatusDot()
    {
        Classes.Add("Dot");
        UpdateToneClass(Tone);
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty)
        {
            UpdateToneClass(change.GetNewValue<StatusTone>());
        }
    }

    private void UpdateToneClass(StatusTone tone)
    {
        Classes.Remove("Ok");
        Classes.Remove("Running");
        Classes.Remove("Warn");
        switch (tone)
        {
            case StatusTone.Ok:
                Classes.Add("Ok");
                break;
            case StatusTone.Running:
                Classes.Add("Running");
                break;
            case StatusTone.Warn:
                Classes.Add("Warn");
                break;
        }
    }
}
