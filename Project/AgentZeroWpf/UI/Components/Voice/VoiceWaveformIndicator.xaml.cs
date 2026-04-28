using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AgentZeroWpf.UI.Components.Voice;

/// <summary>
/// Compact voice-wave indicator — a horizontal row of vertical bars whose
/// heights track a rolling RMS history, wrapped in a DropShadowEffect that
/// breathes (cyan → magenta) only while <see cref="IsActive"/> is true.
///
/// Visual idiom is lifted from <c>D:\pencil-creator\design\xaml\sample\21-radial-voice-wave.xaml</c>
/// (staggered amplitude pulses) collapsed onto a linear strip so the control
/// fits inline next to the AgentBot input box rather than dominating the
/// panel like the 400×400 radial original.
///
/// Usage:
///   - <see cref="Push"/>   — feed each NAudio RMS sample (capture thread is
///                            fine; the call marshals to UI internally)
///   - <see cref="IsActive"/> — toggles the breathing glow + color cycle
///
/// Bar count, gap, max height all live as XAML defaults; the rolling history
/// is sized to match.
/// </summary>
public partial class VoiceWaveformIndicator : UserControl
{
    private const int BarCount = 9;
    private const double BarGap = 2.0;
    private const double MinBarHeight = 3.0;
    private const double MaxBarHeight = 22.0;

    private readonly System.Windows.Shapes.Rectangle[] _barElements = new System.Windows.Shapes.Rectangle[BarCount];
    private readonly double[] _barHistory = new double[BarCount];

    private Storyboard? _glowStoryboard;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(VoiceWaveformIndicator),
            new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>
    /// When true the breathing glow runs and bars stay rendered; when false
    /// the glow stops, the rolling history clears, and bars collapse to the
    /// idle minimum height. Bind/set from the AgentBot mic toggle.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public VoiceWaveformIndicator()
    {
        InitializeComponent();
        BuildBars();
        Loaded += (_, _) => ApplyActiveState(IsActive);
        Unloaded += (_, _) => StopGlow();
    }

    private void BuildBars()
    {
        bars.Children.Clear();
        for (int i = 0; i < BarCount; i++)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Style = (Style)Resources["WaveBarStyle"],
                Margin = new Thickness(i == 0 ? 0 : BarGap, 0, 0, 0),
            };
            _barElements[i] = rect;
            bars.Children.Add(rect);
        }
    }

    /// <summary>
    /// Feed a fresh RMS sample (0~1). Safe to call from any thread.
    /// </summary>
    public void Push(float rms)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<float>(Push), System.Windows.Threading.DispatcherPriority.Render, rms);
            return;
        }

        if (!IsActive) return;

        // Voice rarely exceeds ~0.3 RMS; scale by 3× then cap. Same factor the
        // Voice Test level meter uses so the two indicators agree.
        double normalised = Math.Min(1.0, Math.Max(0.0, rms * 3.0));

        // Shift the history left and push the new sample at the right so the
        // wave reads as a left-to-right scrolling level meter.
        for (int i = 0; i < BarCount - 1; i++)
            _barHistory[i] = _barHistory[i + 1];
        _barHistory[BarCount - 1] = normalised;

        ApplyHistoryToBars();
    }

    private void ApplyHistoryToBars()
    {
        for (int i = 0; i < BarCount; i++)
        {
            var target = MinBarHeight + _barHistory[i] * (MaxBarHeight - MinBarHeight);
            var bar = _barElements[i];

            // Animate height changes briefly so the bars breathe instead of jittering.
            var anim = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(80),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            };
            bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VoiceWaveformIndicator self)
            self.ApplyActiveState((bool)e.NewValue);
    }

    private void ApplyActiveState(bool active)
    {
        if (active)
        {
            StartGlow();
        }
        else
        {
            StopGlow();
            // Drain history so the bars collapse uniformly on toggle-off.
            Array.Clear(_barHistory, 0, _barHistory.Length);
            ApplyHistoryToBars();
        }
    }

    private void StartGlow()
    {
        StopGlow();

        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
        };

        var blur = new DoubleAnimation
        {
            From = 6, To = 18,
            Duration = TimeSpan.FromSeconds(1.4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(blur, glow);
        Storyboard.SetTargetProperty(blur, new PropertyPath(DropShadowEffect.BlurRadiusProperty));
        sb.Children.Add(blur);

        var opacity = new DoubleAnimation
        {
            From = 0.45, To = 0.9,
            Duration = TimeSpan.FromSeconds(1.4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(opacity, glow);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(DropShadowEffect.OpacityProperty));
        sb.Children.Add(opacity);

        // Color cycle on a slower beat than blur/opacity — cyan ↔ magenta.
        var colorCycle = new ColorAnimation
        {
            From = (Color)ColorConverter.ConvertFromString("#3794FF"),
            To = (Color)ColorConverter.ConvertFromString("#C586C0"),
            Duration = TimeSpan.FromSeconds(2.8),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(colorCycle, glow);
        Storyboard.SetTargetProperty(colorCycle, new PropertyPath(DropShadowEffect.ColorProperty));
        sb.Children.Add(colorCycle);

        sb.Begin(this, true);
        _glowStoryboard = sb;
    }

    private void StopGlow()
    {
        if (_glowStoryboard is not null)
        {
            try { _glowStoryboard.Stop(this); } catch { }
            _glowStoryboard = null;
        }
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        glow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        glow.BlurRadius = 0;
        glow.Opacity = 0;
    }
}
