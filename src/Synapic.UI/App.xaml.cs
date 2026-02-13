using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using Synapic.Application.Configuration;

namespace Synapic.UI;

/// <summary>
/// App.xaml logic
/// </summary>
public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Configure services
            var services = new ServiceCollection();
            services.AddSynapicServices(options =>
            {
                options.ModelCachePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Synapic", "Models");
                options.SessionStoragePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Synapic", "Sessions");
            });
            
            // Add logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.AddDebug();
            });
            
            _serviceProvider = services.BuildServiceProvider();
            
            // Create and show main window
            var mainWindow = new MainWindow(_serviceProvider);
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup Error: {ex.Message}\n\n{ex.StackTrace}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    
    protected override void OnExit(ExitEventArgs e)
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}