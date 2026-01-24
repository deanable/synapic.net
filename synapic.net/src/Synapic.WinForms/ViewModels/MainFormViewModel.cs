using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapic.Application.Services;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;

namespace Synapic.WinForms.ViewModels;

/// <summary>
/// Main form view model (adapted from WPF implementation)
/// </summary>
public class MainFormViewModel : INotifyPropertyChanged
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainFormViewModel> _logger;
    private readonly Form _owner;
    private ProcessingManager? _processingManager;
    
    private bool _isProcessing;
    private int _currentProgress;
    private string _statusMessage = "Ready";
    private readonly BindingList<ProcessingResult> _results = new();
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainFormViewModel(IServiceProvider serviceProvider, ILogger<MainFormViewModel> logger, Form owner)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _owner = owner;
    }

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
    
    public BindingList<ProcessingResult> Results => _results;
    
    // Data Source Properties
    public DataSourceType SelectedDataSourceType
    {
        get => _selectedDataSourceType;
        set
        {
            if (SetProperty(ref _selectedDataSourceType, value))
            {
                OnPropertyChanged(nameof(IsLocalDataSource));
                OnPropertyChanged(nameof(IsDaminionDataSource));
            }
        }
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
        set
        {
             if (SetProperty(ref _selectedEngineProvider, value))
             {
                 OnPropertyChanged(nameof(IsLocalEngine));
             }
        }
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
        set
        {
            if (SetProperty(ref _deviceId, value))
            {
                OnPropertyChanged(nameof(IsDeviceCpu));
                OnPropertyChanged(nameof(IsDeviceGpu0));
                OnPropertyChanged(nameof(IsDeviceGpu1));
            }
        }
    }
    
    // Helper Properties for Radio Buttons
    public bool IsLocalDataSource
    {
        get => SelectedDataSourceType == DataSourceType.Local;
        set { if (value) SelectedDataSourceType = DataSourceType.Local; }
    }

    public bool IsDaminionDataSource
    {
        get => SelectedDataSourceType == DataSourceType.Daminion;
        set { if (value) SelectedDataSourceType = DataSourceType.Daminion; }
    }
    
    public bool IsLocalEngine => SelectedEngineProvider == EngineProvider.Local;

    public bool IsDeviceCpu
    {
        get => DeviceId == -1;
        set { if (value) DeviceId = -1; }
    }
    
    public bool IsDeviceGpu0
    {
        get => DeviceId == 0;
        set { if (value) DeviceId = 0; }
    }
    
    public bool IsDeviceGpu1
    {
        get => DeviceId == 1;
        set { if (value) DeviceId = 1; }
    }

    // Actions
    public async Task StartProcessingAsync()
    {
        if (IsProcessing) return;
        
        if (!ValidateConfiguration())
            return;
            
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
            
            _processingManager = _serviceProvider.GetRequiredService<ProcessingManager>();
            
            _processingManager.LogMessage += OnLogMessage;
            _processingManager.ProgressChanged += OnProgressChanged;
            
            IsProcessing = true;
            StatusMessage = "Initializing...";
            CurrentProgress = 0;
            
            await _processingManager.StartProcessingAsync(_session);
            
            StatusMessage = "Processing completed successfully";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during processing");
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"Processing failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            IsProcessing = false;
            CleanupProcessing();
        }
    }
    
    public async Task StopProcessingAsync()
    {
        if (_processingManager != null && IsProcessing)
        {
            await _processingManager.StopProcessingAsync();
            StatusMessage = "Processing cancelled";
        }
    }
    
    public void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Image Folder",
            InitialDirectory = string.IsNullOrEmpty(LocalPath) ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : LocalPath,
            UseDescriptionForTitle = true
        };
        
        if (dialog.ShowDialog(_owner) == DialogResult.OK)
        {
            LocalPath = dialog.SelectedPath;
        }
    }
    
    public void ClearResults()
    {
        Results.Clear();
        StatusMessage = "Results cleared";
    }

    private void OnLogMessage(object? sender, string message)
    {
        _owner.Invoke(() => StatusMessage = message);
    }
    
    private void OnProgressChanged(object? sender, ProgressEventArgs e)
    {
        _owner.Invoke(() =>
        {
            CurrentProgress = (int)e.Percentage;
            StatusMessage = $"Processing {e.Current} of {e.Total} items";
        });
    }
    
    private void CleanupProcessing()
    {
        if (_processingManager != null)
        {
            _processingManager.LogMessage -= OnLogMessage;
            _processingManager.ProgressChanged -= OnProgressChanged;
            
            // Add results to the collection
            _owner.Invoke(() => {
                foreach (var result in _session.Results)
                {
                    Results.Add(result);
                }
            });
        }
    }
    
    private bool ValidateConfiguration()
    {
        if (SelectedDataSourceType == DataSourceType.Local && string.IsNullOrEmpty(LocalPath))
        {
            MessageBox.Show("Please select a local folder path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        
        if (SelectedDataSourceType == DataSourceType.Daminion && 
            (string.IsNullOrEmpty(DaminionUrl) || string.IsNullOrEmpty(DaminionUser) || string.IsNullOrEmpty(DaminionPassword)))
        {
            MessageBox.Show("Please complete Daminion connection details.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        
        if (string.IsNullOrEmpty(ModelId))
        {
            MessageBox.Show("Please select a model.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
