# Synapic.NET - Architecture Overview

## Project Structure

The solution follows **Clean Architecture** principles with four main layers:

### 1. **Synapic.Core** (Domain Layer)
- **No external dependencies**
- Contains domain entities and business interfaces
- **Entities:**
  - `MediaItem`: Represents an image with metadata
  - `ProcessingSession`: Session state and configuration
  - `DataSourceConfig`: Data source configuration
  - `EngineConfig`: AI engine configuration
  - `ProcessingResult`: Result of processing a single item

- **Interfaces:**
  - `IDataSourceProvider`: Contract for data sources (local files, Daminion)
  - `IModelInferenceEngine`: Contract for AI inference engines
  - `IImageMetadataService`: Contract for metadata operations
  - `ISessionRepository`: Contract for session persistence

### 2. **Synapic.Infrastructure** (Infrastructure Layer)
- **Depends on:** Synapic.Core
- **External Dependencies:** TorchSharp, ImageSharp, MetadataExtractor
- Implements all interfaces defined in Core

**Implementations:**
- **AI/**
  - `TorchSharpInferenceEngine`: TorchSharp-based local model inference
  
- **DataSources/**
  - `LocalFileSystemProvider`: Scan local directories for images
  - `DaminionProvider`: Connect to Daminion DAMS API
  
- **Services/**
  - `ImageMetadataService`: Read/write EXIF and IPTC metadata
  
- **Persistence/**
  - `JsonSessionRepository`: JSON-based session storage

### 3. **Synapic.Application** (Application Layer)
- **Depends on:** Synapic.Core, Synapic.Infrastructure
- Orchestrates business workflows
- **Services:**
  - `ProcessingManager`: Main workflow orchestrator
    - Fetches items from data source
    - Initializes AI model
    - Processes each item
    - Writes metadata
    - Reports progress

### 4. **Synapic.UI** (Presentation Layer)
- **Depends on:** Synapic.Application
- WPF-based user interface
- MVVM pattern
- Interacts with Application services

## Dependency Flow

```
UI → Application → Infrastructure → Core
                        ↓
                  External Libraries
                  (TorchSharp, etc.)
```

## Key Design Patterns

1. **Dependency Inversion**: High-level modules depend on abstractions, not implementations
2. **Repository Pattern**: `ISessionRepository` for data persistence
3. **Strategy Pattern**: Different `IDataSourceProvider` implementations
4. **Factory Pattern**: Model creation based on task type
5. **Observer Pattern**: Events for progress reporting

## Benefits

- **Testability**: Each layer can be unit tested independently
- **Maintainability**: Clear separation of concerns
- **Flexibility**: Easy to swap implementations
- **Scalability**: New features don't affect existing code
- **Reusability**: Core logic can be reused in different UIs (WPF, Web, Console)

## Extension Points

### Adding a New Data Source
1. Create class implementing `IDataSourceProvider` in Infrastructure
2. Register in DI container
3. Update UI to support new source

### Adding a New AI Engine
1. Create class implementing `IModelInferenceEngine` in Infrastructure
2. Add provider-specific logic
3. Register in DI container

### Adding New Metadata Fields
1. Update `MediaItem` entity in Core
2. Update `IImageMetadataService` interface
3. Implement in `ImageMetadataService`

## Technology Stack

- **.NET 10.0**
- **TorchSharp 0.103.x**: .NET bindings for PyTorch
- **SixLabors.ImageSharp**: Image processing
- **MetadataExtractor**: Read image metadata
- **WPF**: User interface
- **Microsoft.Extensions.Logging**: Logging infrastructure

## Future Enhancements

- Add more AI model providers (OpenRouter, Hugging Face API)
- Support for video processing
- Batch export capabilities
- Cloud storage integration
- REST API for headless operation
- Docker containerization
