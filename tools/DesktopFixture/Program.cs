using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var window = new Window { Title = "PinyinTranslate Synthetic Fixture", Width = 960, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(35) };
        panel.Children.Add(new TextBlock { Text = "LAB NOTES", FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "1. Store samples below 20 °C.\n2. Do not open the container for 48 hours.", FontSize = 23, Margin = new Thickness(0, 0, 0, 30) });
        var text = new TextBox { Name = "FixtureText", Text = "If the temperature does not exceed 20 °C, store the samples for 48 hours.", FontSize = 20, MinHeight = 55, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6) };
        panel.Children.Add(text);
        panel.Children.Add(new TextBlock { Text = "Synthetic sample: selecting text here is permitted for this test.", FontSize = 15, Margin = new Thickness(0, 20, 0, 0) });
        window.Content = panel; app.Run(window);
    }
}
