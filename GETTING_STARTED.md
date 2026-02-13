# Synapic.NET - Getting Started Guide

## Overview

Synapic.NET is a complete rewrite of the Python-based Synapic image metadata tagging system, built using .NET 10 and TorchSharp for AI-powered image analysis.

## Solution Structure

```
synapic.net/
├── Synapic.sln                    # Solution file
├── README.md                      # Main documentation
├── ARCHITECTURE.md                # Architecture details
├── .gitignore                     # Git ignore file
└── src/
    ├── Synapic.Core/              # Domain layer (no dependencies)
    │   ├── Entities/
    │   │   ├── MediaItem.cs
    │   │   └── ProcessingSession.cs
    │   └── Interfaces/
    │       ├── IDataSourceProvider.cs
    │       ├── IModelInferenceEngine.cs
    │       ├── IImageMetadataService.cs
    │       └── ISessionRepository.cs
    │
    ├── Synapic.Infrastructure/    # Infrastructure implementations
    │   ├── AI/
    │   │   └── TorchSharpInferenceEngine.cs
    │   ├── DataSources/
    │   │   ├── LocalFileSystemProvider.cs
    │   │   └── DaminionProvider.cs
    │   ├── Services/
    │   │   └── ImageMetadataService.cs
    │   └── Persistence/
    │       └── JsonSessionRepository.cs
    │
    ├── Synapic.Application/       # Business logic orchestration
    │   └── Services/
    │       └── ProcessingManager.cs
    │
    └── Synapic.UI/                # WPF user interface
        └── (WPF application files)
```

## Key Features Implemented

### ✅ Clean Architecture
- Clear separation of concerns across 4 layers
- Dependency inversion principle
- Testable and maintainable code

### ✅ Data Source Abstraction
- **LocalFileSystemProvider**: Scan local directories for images
- **DaminionProvider**: Connect to Daminion DAMS API
- Extensible interface for adding new sources

### ✅ AI Inference with TorchSharp
- **TorchSharpInferenceEngine**: Native .NET deep learning
- Support for CPU and CUDA GPU
- Placeholder models for Image Classification and Image-to-Text
- Ready for integration with actual pre-trained models

### ✅ Metadata Management
- **ImageMetadataService**: Read/write EXIF and IPTC metadata
- Support for categories, keywords, and descriptions
- Retry logic for file locking scenarios

### ✅ Session Persistence
- **JsonSessionRepository**: Save/load configuration
- Stored in user's AppData folder

### ✅ Processing Workflow
- **ProcessingManager**: Orchestrates entire workflow
- Event-based progress reporting
- Cancellation support
- Error handling and logging

## Building the Solution

```bash
# Navigate to solution directory
cd synapic.net

# Restore NuGet packages
dotnet restore

# Build the solution
dotnet build

# Run the UI (when implemented)
dotnet run --project src/Synapic.UI/Synapic.UI.csproj
```

## NuGet Packages Used

### Core
- No external dependencies (pure domain logic)

### Infrastructure
- **TorchSharp** (0.103.x): .NET bindings for PyTorch/LibTorch
- **TorchSharp-cpu**: CPU-only LibTorch backend
- **SixLabors.ImageSharp**: Cross-platform image processing
- **MetadataExtractor**: Read image metadata (EXIF, IPTC)
- **Microsoft.Extensions.Logging**: Logging infrastructure
- **Microsoft.Extensions.Http**: HTTP client factory

### Application
- **Microsoft.Extensions.Logging**: Logging infrastructure

### UI
- **WPF** (built-in): Windows Presentation Foundation

## Next Steps

### 1. Complete the UI Layer
The WPF UI needs to be implemented with:
- Data source selection (Local/Daminion)
- Model configuration
- Processing controls (Start/Stop/Progress)
- Results display

### 2. Integrate Real AI Models
Replace placeholder models with actual pre-trained models:
- ResNet for image classification
- CLIP/BLIP for image-to-text
- Load pre-trained weights from disk or Hugging Face

### 3. Add Dependency Injection
Set up Microsoft.Extensions.DependencyInjection:
```csharp
services.AddSingleton<IImageMetadataService, ImageMetadataService>();
services.AddSingleton<ISessionRepository, JsonSessionRepository>();
services.AddTransient<IDataSourceProvider, LocalFileSystemProvider>();
services.AddTransient<IModelInferenceEngine, TorchSharpInferenceEngine>();
services.AddScoped<ProcessingManager>();
```

### 4. Implement Unit Tests
Create test projects for each layer:
- `Synapic.Core.Tests`
- `Synapic.Infrastructure.Tests`
- `Synapic.Application.Tests`

### 5. Add More Features
- OpenRouter API integration
- Hugging Face API integration
- Batch export to CSV/Excel
- Cloud storage support (Azure Blob, AWS S3)
- Docker containerization

## Comparison with Python Version

| Feature | Python (Original) | .NET (This Implementation) |
|---------|-------------------|----------------------------|
| Language | Python 3.x | C# / .NET 10 |
| AI Framework | PyTorch (via transformers) | TorchSharp (LibTorch) |
| UI Framework | CustomTkinter | WPF |
| Architecture | Modular scripts | Clean Architecture |
| Image Processing | PIL/Pillow | ImageSharp |
| Metadata | piexif, iptcinfo3 | MetadataExtractor, ImageSharp |
| Logging | Python logging | Microsoft.Extensions.Logging |
| Config Storage | JSON (manual) | JSON (via repository pattern) |
| Dependency Injection | Manual | Microsoft.Extensions.DI (ready) |
| Testing | Manual | xUnit/NUnit (ready) |

## Advantages of .NET Implementation

1. **Type Safety**: Compile-time type checking prevents runtime errors
2. **Performance**: Native code execution, especially with TorchSharp
3. **Tooling**: Excellent IDE support (Visual Studio, Rider, VS Code)
4. **Deployment**: Single-file executables, no Python runtime needed
5. **Enterprise Ready**: Built-in DI, logging, configuration
6. **Scalability**: Easy to add REST API, gRPC, or other interfaces
7. **Cross-Platform**: Runs on Windows, Linux, macOS (except WPF UI)

## Migration Path from Python

If you have existing Python Synapic installations:

1. **Configuration**: Session files are compatible (both use JSON)
2. **Workflow**: Same 4-step process (Data Source → Tagging → Process → Results)
3. **Data Sources**: Both support local files and Daminion
4. **Metadata**: Both write EXIF/IPTC metadata

## Contributing

This is a clean, well-architected foundation ready for:
- Feature additions
- Alternative UI implementations (Blazor, Avalonia, Console)
- Cloud deployment
- Microservices architecture

## License

[Specify your license here]

## Acknowledgments

- Original Python implementation: Synapic
- TorchSharp team for .NET PyTorch bindings
- SixLabors for ImageSharp
- .NET team for excellent framework and tools
