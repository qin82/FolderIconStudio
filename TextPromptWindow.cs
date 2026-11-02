using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FolderIconStudio;

public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;
    private string? _result;

    private TextPromptWindow(string title, string prompt, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51))
        });

        _input = new TextBox
        {
            Text = initial,
            Padding = new Thickness(6, 5, 6, 5),
            FontSize = 14
        };
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            Style = (Style)Application.Current.FindResource("SoftButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };

        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(20, 7, 20, 7)
        };
        ok.Click += (_, _) =>
        {
            _result = _input.Text.Trim();
            DialogResult = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public static string? Show(Window owner, string title, string prompt, string initial)
    {
        var window = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window._result)
            ? window._result
            : null;
    }
}
