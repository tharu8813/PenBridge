using System.Windows;
using System.Windows.Media;

namespace PenBridge.UI;

public partial class ClosingWindow : Window
{
    public ClosingWindow()
    {
        InitializeComponent();
        Background = (Brush)FindResource("WindowBg");
    }
}
