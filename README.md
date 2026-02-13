# Synapic.NET

A .NET implementation of the Synapic image metadata tagging system using TorchSharp for AI-powered image analysis.

## Architecture

This solution follows Clean Architecture principles with clear separation of concerns:

```
Synapic/
├── src/
│   ├── Synapic.Core/              # Domain entities and interfaces
│   │   ├── Entities/              # Domain models
│   │   │   ├── MediaItem.cs
│   │   │   └── ProcessingSession.cs
│   │   └── Interfaces/            # Abstractions
│   │       ├── IDataSourceProvider.cs
│   │       ├── IModelInferenceEngine.cs
│   │       ├── IImageMetadataService.cs
│   │       └── ISessionRepository.cs
│   │
│   ├── Synapic.Infrastructure/    # External concerns implementation
│   │   ├── AI/                    # TorchSharp-based AI engines
│   │   │   └── TorchSharpInferenceEngine.cs
│   │   ├── DataSources/           # Data source providers
│   │   │   ├── LocalFileSystemProvider.cs
│   │   │   └── DaminionProvider.cs
│   │   ├── Services/              # Infrastructure services
│   │   │   └── ImageMetadataService.cs
│   │   └── Persistence/           # Data persistence
│   │       └── JsonSessionRepository.cs
│   │
│   ├── Synapic.Application/       # Business logic orchestration
│   │   └── Services/
│   │       └── ProcessingManager.cs
│   │
│   └── Synapic.UI/                # WPF User Interface
│       └── (WPF application)
```

## Key Features

### 1. **Data Source Abstraction**
- **Local File System**: Scan directories for images with filtering
- **Daminion DAMS**: Connect to Daminion Digital Asset Management System
- Extensible interface for adding new data sources

### 2. **AI-Powered Image Analysis**
- **TorchSharp Integration**: Native .NET deep learning using LibTorch
- Support for multiple model tasks:
  - Image Classification
  - Image-to-Text (Captioning)
  - Object Detection
  - Zero-Shot Classification
- CPU and CUDA GPU support

### 3. **Metadata Management**
- Read/Write EXIF and IPTC metadata
- Support for:
  - Categories
  - Keywords
  - Descriptions
- Retry logic for file locking scenarios

### 4. **Session Persistence**
- Save and restore configuration
- JSON-based storage in AppData

## Technology Stack

- **.NET 10.0**: Latest .NET framework
- **TorchSharp**: .NET bindings for PyTorch/LibTorch
- **SixLabors.ImageSharp**: Cross-platform image processing
- **MetadataExtractor**: Read image metadata
- **WPF**: Windows Presentation Foundation for UI
- **Microsoft.Extensions.Logging**: Structured logging

## Getting Started

### Prerequisites

- .NET 10.0 SDK or later
- Windows 10/11 (for WPF UI)
- Optional: NVIDIA GPU with CUDA support for GPU acceleration

### Building the Solution

```bash
# Clone the repository
cd synapic.net

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run the UI application
dotnet run --project src/Synapic.UI/Synapic.UI.csproj
```

## Usage Example

```csharp
using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Infrastructure.AI;
using Synapic.Infrastructure.Services;
using Synapic.Infrastructure.DataSources;
using Synapic.Application.Services;

// Setup logging
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

// Create services
var imageService = new ImageMetadataService(
    loggerFactory.CreateLogger<ImageMetadataService>());

var inferenceEngine = new TorchSharpInferenceEngine(
    loggerFactory.CreateLogger<TorchSharpInferenceEngine>(),
    imageService);

var dataSource = new LocalFileSystemProvider(
    loggerFactory.CreateLogger<LocalFileSystemProvider>(),
    imageService);

// Create session
var session = new ProcessingSession
{
    DataSource = new DataSourceConfig
    {
        Type = DataSourceType.Local,
        LocalPath = @"C:\Photos",
        LocalRecursive = true,
        MaxItems = 100
    },
    Engine = new EngineConfig
    {
        Provider = EngineProvider.Local,
        ModelId = "resnet50",
        Task = ModelTask.ImageClassification,
        DeviceId = -1 // CPU
    }
};

// Create processing manager
var processingManager = new ProcessingManager(
    loggerFactory.CreateLogger<ProcessingManager>(),
    dataSource,
    inferenceEngine,
    imageService,
    session);

// Subscribe to events
processingManager.LogMessage += (s, msg) => Console.WriteLine(msg);
processingManager.ProgressChanged += (s, e) => 
    Console.WriteLine($"Progress: {e.Percentage:F1}%");

// Start processing
await processingManager.StartProcessingAsync();
```

## Project Structure Rationale

### Synapic.Core (Domain Layer)
- **No dependencies** on external frameworks
- Contains pure business logic and domain models
- Defines interfaces (contracts) for infrastructure

### Synapic.Infrastructure (Infrastructure Layer)
- **Depends on**: Synapic.Core
- Implements interfaces defined in Core
- Contains all external dependencies (TorchSharp, ImageSharp, etc.)
- Handles:
  - AI model inference
  - File system operations
  - External API calls (Daminion)
  - Metadata reading/writing

### Synapic.Application (Application Layer)
- **Depends on**: Synapic.Core, Synapic.Infrastructure
- Orchestrates business workflows
- Contains application services like ProcessingManager
- Coordinates between domain and infrastructure

### Synapic.UI (Presentation Layer)
- **Depends on**: Synapic.Application
- WPF-based user interface
- MVVM pattern for clean separation
- View models interact with Application services

## Benefits of This Architecture

1. **Testability**: Each layer can be tested independently
2. **Maintainability**: Clear separation makes code easier to understand
3. **Flexibility**: Easy to swap implementations (e.g., different AI engines)
4. **Scalability**: New features can be added without affecting existing code
5. **Dependency Inversion**: High-level modules don't depend on low-level modules

## Extending the System

### Adding a New Data Source

1. Implement `IDataSourceProvider` in Synapic.Infrastructure
2. Register in dependency injection container
3. Update UI to support new source type

### Adding a New AI Engine

1. Implement `IModelInferenceEngine` in Synapic.Infrastructure
2. Add provider-specific logic (API calls, model loading, etc.)
3. Register in DI container

### Adding New Model Tasks

1. Add enum value to `ModelTask` in ProcessingSession.cs
2. Implement task-specific logic in inference engine
3. Update UI to expose new task option

## Future Enhancements

- [ ] Support for video processing
- [ ] Batch export to CSV/Excel
- [ ] Cloud storage integration (Azure Blob, AWS S3)
- [ ] REST API for headless operation
- [ ] Docker containerization
- [ ] Model fine-tuning capabilities
- [ ] Multi-language support

## License

[Your License Here]

## Contributing

Contributions are welcome! Please follow the existing architecture patterns and coding standards.

## Acknowledgments

- Original Python implementation: Synapic
- TorchSharp: .NET bindings for PyTorch
- SixLabors.ImageSharp: Cross-platform image processing
