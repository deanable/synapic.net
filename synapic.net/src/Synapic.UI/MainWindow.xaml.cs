using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using Synapic.Application.Configuration;
using Synapic.UI.ViewModels;

namespace Synapic.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _serviceProvider;
    
    public MainWindow(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        InitializeComponent();
        
        // Set DataContext with dependencies from service provider
        var logger = _serviceProvider.GetRequiredService<ILogger<MainViewModel>>();
        DataContext = new MainViewModel(_serviceProvider, logger);
    }
    
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Set up logging to output to debug window
        var logger = _serviceProvider.GetRequiredService<ILogger<MainWindow>>();
        logger.LogInformation("Synapic.NET UI started");
    }
}