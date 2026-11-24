using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AutoKey;

/// <summary>AutoKey's version and what it does, opened from the tray menu.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Version}";
    }

    /// <summary>The version AutoKey was built as, without the source revision the SDK appends after a "+".</summary>
    private static string Version
    {
        get
        {
            Assembly assembly = typeof(AboutWindow).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? assembly.GetName().Version?.ToString(3) ?? "";
            int metadata = version.IndexOf('+');
            return metadata >= 0 ? version[..metadata] : version;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
