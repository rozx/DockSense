using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DockSense.App.ViewModels;

namespace DockSense.App.Views;

/// <summary>
/// A slider with an input box next to it, for choosing one of a list of options (<see cref="IOption"/>): each
/// option is one stop of the slider, and the box shows the option's label and takes a typed number or label.
/// <para>
/// The choice is passed on (<see cref="SelectedValue"/>) only when it is final: when the slider is let go, a
/// moment after it was moved by keyboard or a click, or when the typed text is confirmed with Enter or by leaving
/// the box. The options here write to the firmware or ask for confirmation, which must not happen for every stop
/// a drag passes.
/// </para>
/// </summary>
public sealed partial class OptionSlider : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable), typeof(OptionSlider), new PropertyMetadata(null, (d, _) => ((OptionSlider)d).ShowSelection()));

    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(object), typeof(OptionSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((OptionSlider)d).ShowSelection()));

    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(400);

    private readonly Slider _slider = new()
    {
        Minimum = 0,
        TickFrequency = 1,
        SmallChange = 1,
        LargeChange = 1,
        IsSnapToTickEnabled = true,
        IsMoveToPointEnabled = true,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Wpf.Ui.Controls.TextBox _input = new()
    {
        Width = 120,
        Margin = new Thickness(12, 0, 0, 0),
        ClearButtonEnabled = false,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly DispatcherTimer _commitTimer = new() { Interval = CommitDelay };
    private IReadOnlyList<IOption> _options = [];
    private bool _showing;
    private bool _dragging;

    public OptionSlider()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_input, 1);
        grid.Children.Add(_slider);
        grid.Children.Add(_input);
        Content = grid;
        Focusable = false;

        _slider.ValueChanged += (_, _) => OnSliderMoved();
        _slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            Commit();
        }));
        _commitTimer.Tick += (_, _) =>
        {
            if (!_dragging)
            {
                Commit(); // While the slider is held, letting go commits.
            }
        };

        // Typing replaces what is there. A click that brings the focus would otherwise place the caret inside the
        // old text, and the new digits would land in the middle of "20 W".
        _input.GotKeyboardFocus += (_, _) => _input.SelectAll();
        _input.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_input.IsKeyboardFocusWithin)
            {
                _input.Focus();
                e.Handled = true;
            }
        };
        _input.LostKeyboardFocus += (_, _) => CommitText();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                _input.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ShowLabel();
                _input.SelectAll();
                e.Handled = true;
            }
        };

        Loaded += (_, _) =>
        {
            // Screen readers get the row's name on both parts.
            var name = AutomationProperties.GetName(this);
            AutomationProperties.SetName(_slider, name);
            AutomationProperties.SetName(_input, name);
        };
        Unloaded += (_, _) =>
        {
            // Leaving the page within the moment a click or a key press waits to be passed on must not lose it:
            // the slider would show one value and the profile keep another.
            if (_commitTimer.IsEnabled)
            {
                Commit();
            }
        };
    }

    /// <summary>The options, in slider order (a list of <see cref="IOption"/>).</summary>
    public IEnumerable? Options
    {
        get => (IEnumerable?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The chosen option's value. Changed by the control only when a choice is final.</summary>
    public object? SelectedValue
    {
        get => GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    private int Index => Math.Clamp((int)Math.Round(_slider.Value), 0, Math.Max(0, _options.Count - 1));

    /// <summary>
    /// The option a typed text means, or -1: a label is that option, and a number is the option whose number is
    /// closest. The whole text has to be one plain number, with the options' unit after it at most ("20", "20 W"):
    /// "-20" or "20-30" mean nothing here, and must not turn into 20. A Chinese input method types "。" for the
    /// decimal point.
    /// </summary>
    internal static int Resolve(IReadOnlyList<IOption> options, string text)
    {
        var typed = text.Trim();
        if (options.Count == 0)
        {
            return -1;
        }

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Label, typed, StringComparison.CurrentCultureIgnoreCase))
            {
                return i;
            }
        }

        var match = NumberPattern().Match(typed.Replace('。', '.').Replace(',', '.'));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return -1;
        }

        // A unit has to be the one the options are shown in: "20 mW" is not 20 W.
        var unit = match.Groups[2].Value;
        if (unit.Length > 0 && !options.Any(o => NumberPattern().Match(o.Label) is { Success: true } label
            && string.Equals(label.Groups[2].Value, unit, StringComparison.CurrentCultureIgnoreCase)))
        {
            return -1;
        }

        var best = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Number is { } candidate
                && (best < 0 || Math.Abs(candidate - number) < Math.Abs(options[best].Number!.Value - number)))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Brings slider and box in line with <see cref="Options"/> and <see cref="SelectedValue"/>.</summary>
    private void ShowSelection()
    {
        _options = Options?.OfType<IOption>().ToList() ?? [];
        _commitTimer.Stop();
        _showing = true;
        try
        {
            _slider.Maximum = Math.Max(0, _options.Count - 1);
            var index = -1;
            for (var i = 0; i < _options.Count && index < 0; i++)
            {
                index = Equals(_options[i].Value, SelectedValue) ? i : -1;
            }

            _slider.Value = Math.Max(0, index);
        }
        finally
        {
            _showing = false;
        }

        ShowLabel();
    }

    private void ShowLabel()
    {
        var label = _options.Count > 0 ? _options[Index].Label : string.Empty;
        _input.Text = label;
        _input.ToolTip = label;
    }

    private void OnSliderMoved()
    {
        if (_showing)
        {
            return;
        }

        ShowLabel();
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void Commit()
    {
        _commitTimer.Stop();
        if (_options.Count > 0 && !Equals(_options[Index].Value, SelectedValue))
        {
            // SetCurrentValue keeps the binding and sends the value to its source.
            SetCurrentValue(SelectedValueProperty, _options[Index].Value);
        }
    }

    private void CommitText()
    {
        if (_options.Count == 0 || _input.Text == _options[Index].Label)
        {
            return;
        }

        var index = Resolve(_options, _input.Text);
        if (index >= 0)
        {
            _showing = true;
            _slider.Value = index;
            _showing = false;
        }

        ShowLabel(); // Also puts the label back for text that means nothing.
        Commit();
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(\p{L}*)$")]
    private static partial Regex NumberPattern();
}
