using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PenBridge.UI;

public partial class ClientDetailsWindow : Window
{
    private readonly Func<string> _buildText;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClientDetailsWindow(Func<string> buildText, ImageSource? icon)
    {
        InitializeComponent();
        Background = (Brush)FindResource("WindowBg");
        _buildText = buildText;
        if (icon is not null) Icon = icon;
        DetailsText.Text = _buildText();
        _timer.Tick += (_, _) =>
        {
            string value = _buildText();
            if (DetailsText.Text != value) DetailsText.Text = value;
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
