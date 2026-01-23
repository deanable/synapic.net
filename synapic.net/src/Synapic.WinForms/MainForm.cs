using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.WinForms.ViewModels;

namespace Synapic.WinForms;

public partial class MainForm : Form
{
    private readonly MainFormViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainForm> _logger;
    
    // Controls
    private RadioButton _rbDataSourceLocal;
    private RadioButton _rbDataSourceDaminion;
    private TextBox _txtLocalPath;
    private CheckBox _chkRecursive;
    private TextBox _txtDaminionUrl;
    private TextBox _txtDaminionUser;
    private TextBox _txtDaminionPassword;
    private NumericUpDown _nudMaxItems;
    
    private ComboBox _cboEngineProvider;
    private TextBox _txtModelId;
    private ComboBox _cboModelTask;
    private RadioButton _rbDeviceCpu;
    private RadioButton _rbDeviceGpu0;
    private RadioButton _rbDeviceGpu1;
    
    private Button _btnBrowse;
    private Button _btnStart;
    private Button _btnStop;
    private Button _btnClear;
    
    private ProgressBar _progressBar;
    private TextBox _txtStatus;
    private DataGridView _gridResults;

    public MainForm(IServiceProvider serviceProvider, ILogger<MainForm> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _viewModel = new MainFormViewModel(serviceProvider, _serviceProvider.GetRequiredService<ILogger<MainFormViewModel>>(), this);

        InitializeComponent();
        InitializeBindings();
    }

    private void InitializeComponent()
    {
        this.Text = "Synapic.NET (WinForms)";
        this.Size = new Size(1000, 800);
        this.StartPosition = FormStartPosition.CenterScreen;

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180)); // Data Source
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); // Engine
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Actions
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Output
        
        // 1. Data Source Group
        var grpDataSource = new GroupBox { Text = "Data Source", Dock = DockStyle.Fill };
        var flowDataSource = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        
        // Type Selection
        var pnlSourceType = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _rbDataSourceLocal = new RadioButton { Text = "Local File System", Checked = true, AutoSize = true };
        _rbDataSourceDaminion = new RadioButton { Text = "Daminion DAMS", AutoSize = true };
        pnlSourceType.Controls.AddRange(new Control[] { _rbDataSourceLocal, _rbDataSourceDaminion });
        
        // Local Config
        var pnlLocal = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Width = 900 };
        _txtLocalPath = new TextBox { Width = 400, PlaceholderText = "Folder path..." };
        _btnBrowse = new Button { Text = "Browse..." };
        _chkRecursive = new CheckBox { Text = "Recursive", Checked = true, AutoSize = true };
        pnlLocal.Controls.AddRange(new Control[] { new Label { Text = "Path:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Height = 23 }, _txtLocalPath, _btnBrowse, _chkRecursive });
        
        // Daminion Config
        var pnlDaminion = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Width = 900 };
        _txtDaminionUrl = new TextBox { Width = 200, PlaceholderText = "URL" };
        _txtDaminionUser = new TextBox { Width = 150, PlaceholderText = "Username" };
        _txtDaminionPassword = new TextBox { Width = 150, PasswordChar = '*', PlaceholderText = "Password" };
        _nudMaxItems = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = 100 };
        pnlDaminion.Controls.AddRange(new Control[] {
            new Label { Text = "URL:", AutoSize = true }, _txtDaminionUrl,
            new Label { Text = "User:", AutoSize = true }, _txtDaminionUser,
            new Label { Text = "Pass:", AutoSize = true }, _txtDaminionPassword,
            new Label { Text = "Max:", AutoSize = true }, _nudMaxItems
        });

        flowDataSource.Controls.AddRange(new Control[] { pnlSourceType, pnlLocal, pnlDaminion });
        grpDataSource.Controls.Add(flowDataSource);
        mainLayout.Controls.Add(grpDataSource, 0, 0);

        // 2. Engine Group
        var grpEngine = new GroupBox { Text = "Engine Configuration", Dock = DockStyle.Fill };
        var flowEngine = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        
        var pnlEngineRow1 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Width = 900 };
        _cboEngineProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _cboEngineProvider.DataSource = Enum.GetValues(typeof(EngineProvider));
        _txtModelId = new TextBox { Width = 200, Text = "resnet50" };
        _cboModelTask = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _cboModelTask.DataSource = Enum.GetValues(typeof(ModelTask));
        
        pnlEngineRow1.Controls.AddRange(new Control[] {
            new Label { Text = "Provider:", AutoSize = true }, _cboEngineProvider,
            new Label { Text = "Model ID:", AutoSize = true }, _txtModelId,
            new Label { Text = "Task:", AutoSize = true }, _cboModelTask
        });
        
        var pnlDevice = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _rbDeviceCpu = new RadioButton { Text = "CPU", Checked = true, AutoSize = true };
        _rbDeviceGpu0 = new RadioButton { Text = "GPU 0", AutoSize = true };
        _rbDeviceGpu1 = new RadioButton { Text = "GPU 1", AutoSize = true };
        pnlDevice.Controls.AddRange(new Control[] { new Label { Text = "Device:", AutoSize = true }, _rbDeviceCpu, _rbDeviceGpu0, _rbDeviceGpu1 });
        
        flowEngine.Controls.AddRange(new Control[] { pnlEngineRow1, pnlDevice });
        grpEngine.Controls.Add(flowEngine);
        mainLayout.Controls.Add(grpEngine, 0, 1);
        
        // 3. Actions
        var pnlActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        _btnStart = new Button { Text = "Start Processing", Width = 150 };
        _btnStop = new Button { Text = "Stop", Width = 100, Enabled = false };
        _btnClear = new Button { Text = "Clear Results", Width = 100 };
        pnlActions.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnClear });
        mainLayout.Controls.Add(pnlActions, 0, 2);
        
        // 4. Output
        var pnlOutput = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        pnlOutput.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // Progress
        pnlOutput.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // Status
        pnlOutput.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Grid
        
        _progressBar = new ProgressBar { Dock = DockStyle.Fill };
        _txtStatus = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        _gridResults = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = true, AllowUserToAddRows = false, ReadOnly = true };
        
        pnlOutput.Controls.Add(_progressBar, 0, 0);
        pnlOutput.Controls.Add(_txtStatus, 0, 1);
        pnlOutput.Controls.Add(_gridResults, 0, 2);
        
        mainLayout.Controls.Add(pnlOutput, 0, 3);
        
        this.Controls.Add(mainLayout);
        
        // Event Handlers
        _btnBrowse.Click += (s, e) => _viewModel.BrowseFolder();
        _btnStart.Click += async (s, e) => await _viewModel.StartProcessingAsync();
        _btnStop.Click += async (s, e) => await _viewModel.StopProcessingAsync();
        _btnClear.Click += (s, e) => _viewModel.ClearResults();
    }

    private void InitializeBindings()
    {
        // Data Configuration
        _rbDataSourceLocal.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.IsLocalDataSource), false, DataSourceUpdateMode.OnPropertyChanged);
        _rbDataSourceDaminion.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.IsDaminionDataSource), false, DataSourceUpdateMode.OnPropertyChanged);
        
        _txtLocalPath.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.LocalPath), false, DataSourceUpdateMode.OnPropertyChanged);
        _txtLocalPath.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsLocalDataSource));
        _btnBrowse.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsLocalDataSource));
        _chkRecursive.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.LocalRecursive), false, DataSourceUpdateMode.OnPropertyChanged);
        _chkRecursive.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsLocalDataSource));
        
        _txtDaminionUrl.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.DaminionUrl), false, DataSourceUpdateMode.OnPropertyChanged);
        _txtDaminionUrl.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsDaminionDataSource));
        _txtDaminionUser.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.DaminionUser), false, DataSourceUpdateMode.OnPropertyChanged);
        _txtDaminionUser.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsDaminionDataSource));
        _txtDaminionPassword.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.DaminionPassword), false, DataSourceUpdateMode.OnPropertyChanged);
        _txtDaminionPassword.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsDaminionDataSource));
        _nudMaxItems.DataBindings.Add("Value", _viewModel, nameof(MainFormViewModel.MaxItems), false, DataSourceUpdateMode.OnPropertyChanged);
        _nudMaxItems.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsDaminionDataSource));
        
        // Engine Configuration
        _cboEngineProvider.DataBindings.Add("SelectedItem", _viewModel, nameof(MainFormViewModel.SelectedEngineProvider), false, DataSourceUpdateMode.OnPropertyChanged);
        _txtModelId.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.ModelId), false, DataSourceUpdateMode.OnPropertyChanged);
        _cboModelTask.DataBindings.Add("SelectedItem", _viewModel, nameof(MainFormViewModel.SelectedModelTask), false, DataSourceUpdateMode.OnPropertyChanged);
        
        _rbDeviceCpu.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.IsDeviceCpu), false, DataSourceUpdateMode.OnPropertyChanged);
        _rbDeviceGpu0.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.IsDeviceGpu0), false, DataSourceUpdateMode.OnPropertyChanged);
        _rbDeviceGpu1.DataBindings.Add("Checked", _viewModel, nameof(MainFormViewModel.IsDeviceGpu1), false, DataSourceUpdateMode.OnPropertyChanged);
        
        // Status & Progress
        _txtStatus.DataBindings.Add("Text", _viewModel, nameof(MainFormViewModel.StatusMessage));
        _progressBar.DataBindings.Add("Value", _viewModel, nameof(MainFormViewModel.CurrentProgress));
        
        // Button States
        _btnStart.DataBindings.Add("Enabled", _viewModel, nameof(MainFormViewModel.IsProcessing), true); // Invert binding logic handled? No, wait. 
        // WinForms Simple Binding doesn't support inversion easily inline.
        // Let's use PropertyChanged event to toggle button states for simplicity or create a helper property.
        
        _viewModel.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(MainFormViewModel.IsProcessing))
            {
               _btnStart.Enabled = !_viewModel.IsProcessing;
               _btnStop.Enabled = _viewModel.IsProcessing;
               _rbDataSourceLocal.Enabled = !_viewModel.IsProcessing;
               _rbDataSourceDaminion.Enabled = !_viewModel.IsProcessing;
            }
        };
        
        // Grid
        _gridResults.DataSource = _viewModel.Results;
    }
}
