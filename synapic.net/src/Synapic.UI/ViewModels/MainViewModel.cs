using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapic.Application.Services;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;
using Synapic.UI.Commands;

namespace Synapic.UI.ViewModels;

/// <summary>
/// Main application view model
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainViewModel> _logger;
    private ProcessingManager? _processingManager;
    
    private bool _isProcessing;
    private int _currentProgress;
    private string _statusMessage = "Ready";
    private ObservableCollection<ProcessingResult> _results = new();
    private ProcessingSession _session = new();
    
    // Data Source Properties
    private DataSourceType _selectedDataSourceType = DataSourceType.Local;
    private string _localPath = string.Empty;
    private bool _localRecursive = true;
    private string _daminionUrl = string.Empty;
    private string _daminionUser = string.Empty;
    private string _daminionPassword = string.Empty;
    private int _maxItems = 100;
    
    // Engine Properties
    private EngineProvider _selectedEngineProvider = EngineProvider.Local;
    private string _modelId = "resnet50";
    private ModelTask _selectedModelTask = ModelTask.ImageToText;
    private int _deviceId = -1;
    
    public MainViewModel(IServiceProvider serviceProvider, ILogger<MainViewModel> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        StartProcessingCommand = new AsyncRelayCommand(StartProcessingAsync, CanStartProcessing);
        StopProcessingCommand = new AsyncRelayCommand(StopProcessingAsync, CanStopProcessing);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ClearResultsCommand = new RelayCommand(ClearResults);
    }
    
    // Commands
    public ICommand StartProcessingCommand { get; }
    public ICommand StopProcessingCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand ClearResultsCommand { get; }
    
    // Properties
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }
    
    public int CurrentProgress
    {
        get => _currentProgress;
        set => SetProperty(ref _currentProgress, value);
    }
    
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
    
    public ObservableCollection<ProcessingResult> Results
    {
        get => _results;
        set => SetProperty(ref _results, value);
    }
    
    // Data Source Properties
    public DataSourceType SelectedDataSourceType
    {
        get => _selectedDataSourceType;
        set => SetProperty(ref _selectedDataSourceType, value);
    }
    
    public string LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value);
    }
    
    public bool LocalRecursive
    {
        get => _localRecursive;
        set => SetProperty(ref _localRecursive, value);
    }
    
    public string DaminionUrl
    {
        get => _daminionUrl;
        set => SetProperty(ref _daminionUrl, value);
    }
    
    public string DaminionUser
    {
        get => _daminionUser;
        set => SetProperty(ref _daminionUser, value);
    }
    
    public string DaminionPassword
    {
        get => _daminionPassword;
        set => SetProperty(ref _daminionPassword, value);
    }
    
    public int MaxItems
    {
        get => _maxItems;
        set => SetProperty(ref _maxItems, value);
    }
    
    // Engine Properties
    public EngineProvider SelectedEngineProvider
    {
        get => _selectedEngineProvider;
        set => SetProperty(ref _selectedEngineProvider, value);
    }
    
    public string ModelId
    {
        get => _modelId;
        set => SetProperty(ref _modelId, value);
    }
    
    public ModelTask SelectedModelTask
    {
        get => _selectedModelTask;
        set => SetProperty(ref _selectedModelTask, value);
    }
    
    public int DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }
    
    // Computed Properties
    public bool IsLocalDataSource => SelectedDataSourceType == DataSourceType.Local;
    public bool IsDaminionDataSource => SelectedDataSourceType == DataSourceType.Daminion;
    public bool IsLocalEngine => SelectedEngineProvider == EngineProvider.Local;
    
    private async Task StartProcessingAsync()
    {
        try
        {
            // Configure session
            _session.DataSource = new DataSourceConfig
            {
                Type = SelectedDataSourceType,
                LocalPath = LocalPath,
                LocalRecursive = LocalRecursive,
                DaminionUrl = DaminionUrl,
                DaminionUser = DaminionUser,
                DaminionPassword = DaminionPassword,
                MaxItems = MaxItems
            };
            
            _session.Engine = new EngineConfig
            {
                Provider = SelectedEngineProvider,
                ModelId = ModelId,
                Task = SelectedModelTask,
                DeviceId = DeviceId
            };
            
            // Create processing manager
            _processingManager = _serviceProvider.GetRequiredService<ProcessingManager>();
            
            // Subscribe to events
            _processingManager.LogMessage += OnLogMessage;
            _processingManager.ProgressChanged += (s, e) => App.Current.Dispatcher.Invoke(() =>
        {
            CurrentProgress = (int)e.Percentage;
            StatusMessage = $"Processing {e.Current} of {e.Total} items";
        });
            
            IsProcessing = true;
            StatusMessage = "Initializing...";
            CurrentProgress = 0;
            
            await _processingManager.StartProcessingAsync();
            
            StatusMessage = "Processing completed successfully";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during processing");
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"Processing failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            CleanupProcessing();
        }
    }
    
    private async Task StopProcessingAsync()
    {
        if (_processingManager != null)
        {
            await _processingManager.StopProcessingAsync();
            StatusMessage = "Processing cancelled";
        }
        // No return needed for async void method
    }
    
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Image Folder",
            InitialDirectory = string.IsNullOrEmpty(LocalPath) ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : LocalPath
        };
        
        if (dialog.ShowDialog() == true)
        {
            LocalPath = dialog.FolderName;
        }
    }
    
    private void ClearResults()
    {
        Results.Clear();
        StatusMessage = "Results cleared";
    }
    
    private void OnLogMessage(object? sender, string message)
    {
        App.Current.Dispatcher.Invoke(() => StatusMessage = message);
    }
    
    private void OnProgressChanged(object? sender, (int Percentage, string Message) e)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            CurrentProgress = e.Percentage;
            StatusMessage = e.Message;
        });
    }
    
    private void CleanupProcessing()
    {
        if (_processingManager != null)
        {
            _processingManager.LogMessage -= OnLogMessage;
            // Remove progress event handler
            // Note: Since we used a lambda, we don't need to remove it explicitly
            
            // Add results to the collection
            foreach (var result in _session.Results)
            {
                Results.Add(result);
            }
        }
    }
    
    private bool CanStartProcessing()
    {
        return !IsProcessing && ValidateConfiguration();
    }
    
    private bool CanStopProcessing()
    {
        return IsProcessing;
    }
    
    private bool ValidateConfiguration()
    {
        if (SelectedDataSourceType == DataSourceType.Local && string.IsNullOrEmpty(LocalPath))
        {
            MessageBox.Show("Please select a local folder path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        
        if (SelectedDataSourceType == DataSourceType.Daminion && 
            (string.IsNullOrEmpty(DaminionUrl) || string.IsNullOrEmpty(DaminionUser) || string.IsNullOrEmpty(DaminionPassword)))
        {
            MessageBox.Show("Please complete Daminion connection details.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        
        if (string.IsNullOrEmpty(ModelId))
        {
            MessageBox.Show("Please select a model.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        
        return true;
    }
}