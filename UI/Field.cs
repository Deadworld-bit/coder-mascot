using System.Windows;

namespace CoderMascot.UI;

/// <summary>
/// Placeholder text for a TextBox: what it filters, or what goes in it. Drawn by
/// the TextBox template in Controls.xaml, and only while the box is empty.
/// </summary>
public static class Field
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(Field), new FrameworkPropertyMetadata(string.Empty));

    public static string GetHint(DependencyObject element) => (string)element.GetValue(HintProperty);

    public static void SetHint(DependencyObject element, string value) => element.SetValue(HintProperty, value);
}
