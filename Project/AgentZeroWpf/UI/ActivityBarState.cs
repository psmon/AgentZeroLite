using System.Windows;

namespace AgentZeroWpf.UI;

/// <summary>
/// Which ActivityBar icon is the current one.
///
/// <para>The blue rail down the left of an icon used to be painted by a
/// <c>ColorAnimation</c> on <c>Button.Click</c> — and nothing ever took it off
/// again, so after visiting three pages three icons claimed to be active. An
/// animation is a one-way thing; "which one is selected" is state, and state has
/// to be able to say no.</para>
///
/// <para>An attached property rather than a converter or a ViewModel because the
/// ActivityBar is eight buttons in XAML with no data behind them: the window sets
/// this alongside the page it is showing, and the template's trigger does the
/// rest.</para>
/// </summary>
public static class ActivityBarState
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached(
            "IsSelected", typeof(bool), typeof(ActivityBarState),
            new PropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject o) => (bool)o.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject o, bool value) => o.SetValue(IsSelectedProperty, value);
}
