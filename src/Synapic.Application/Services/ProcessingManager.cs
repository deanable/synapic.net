using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;
using System.Diagnostics;

namespace Synapic.Application.Services;

/// <summary>
/// Service for managing the image processing workflow
/// </summary>
public class ProcessingManager
{
    private readonly ILogger<ProcessingManager> _logger;
    private readonly IDataSourceProvider _dataSourceProvider;
    private readonly IModelInferenceEngine _inferenceEngine;
    private readonly IImageMetadataService _imageMetadataService;
    private ProcessingSession? _session;
    
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<string>? LogMessage;
    public event EventHandler<ProgressEventArgs>? ProgressChanged;
    public event EventHandler<ProcessingResult>? ItemProcessed;
    public event EventHandler? ProcessingCompleted;

    public bool IsProcessing { get; private set; }

    public ProcessingManager(
        ILogger<ProcessingManager> logger,
        IDataSourceProvider dataSourceProvider,
        IModelInferenceEngine inferenceEngine,
        IImageMetadataService imageMetadataService)
    {
        _logger = logger;
        _dataSourceProvider = dataSourceProvider;
        _inferenceEngine = inferenceEngine;
        _imageMetadataService = imageMetadataService;
    }

    public async Task StartProcessingAsync(ProcessingSession session)
    {
        if (IsProcessing)
        {
            _logger.LogWarning("Processing already in progress");
            return;
        }

        _session = session;
        _cancellationTokenSource = new CancellationTokenSource();
        IsProcessing = true;
        if (_session != null) _session.IsProcessing = true;

        _processingTask = Task.Run(async () => await RunProcessingJobAsync(_cancellationTokenSource.Token));
        await _processingTask;
    }

    public async Task StopProcessingAsync()
    {
        if (!IsProcessing || _cancellationTokenSource == null)
            return;

        Log("Stopping processing...");
        _cancellationTokenSource.Cancel();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
                Log("Processing cancelled by user");
            }
        }

        IsProcessing = false;
        if (_session != null) _session.IsProcessing = false;
    }

    private async Task RunProcessingJobAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log("Starting processing job...");
            if (_session == null) throw new InvalidOperationException("Session is null");
            _session.Results.Clear();

            // Step 1: Fetch items
            Log("Fetching items from data source...");
            var items = await FetchItemsAsync(cancellationToken);
            
            if (!items.Any())
            {
                Log("No items to process");
                return;
            }

            _session.TotalItems = items.Count();
            Log($"Found {_session.TotalItems} items to process");

            // Step 2: Initialize model
            Log("Initializing AI model...");
            var modelProgress = new Progress<string>(msg => Log(msg));
            await _inferenceEngine.InitializeAsync(_session.Engine, modelProgress, cancellationToken);

            // Step 3: Process items
            Log("Processing items...");
            int processedCount = 0;

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var result = await ProcessSingleItemAsync(item, cancellationToken);
                    _session.Results.Add(result);
                    
                    if (result.Success)
                    {
                        _session.ProcessedItems++;
                    }
                    else
                    {
                        _session.FailedItems++;
                    }

                    processedCount++;
                    ReportProgress(processedCount, _session.TotalItems);
                    ItemProcessed?.Invoke(this, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing item {ItemId}", item.Id);
                    _session.FailedItems++;
                }
            }

            Log($"Processing completed: {_session.ProcessedItems} successful, {_session.FailedItems} failed");
        }
        catch (OperationCanceledException)
        {
            Log("Processing cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in processing job");
            Log($"Error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
            if (_session != null) _session.IsProcessing = false;
            ProcessingCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<IEnumerable<MediaItem>> FetchItemsAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<int>(count => Log($"Fetched {count} items..."));
        return await _dataSourceProvider.GetItemsAsync(_session.DataSource, progress, cancellationToken);
    }

    private async Task<ProcessingResult> ProcessSingleItemAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new ProcessingResult
        {
            ItemId = item.Id,
            FilePath = item.FilePath
        };

        try
        {
            Log($"Processing: {Path.GetFileName(item.FilePath)}");

            // Validate image
            (bool isValid, string? error) = await _imageMetadataService.ValidateImageAsync(item.FilePath);
            if (!isValid)
            {
                result.Success = false;
                result.Error = error;
                return result;
            }

            // Run AI inference
            (string? category, List<string> keywords, string? description) = await _inferenceEngine.ProcessImageAsync(
                item.FilePath, 
                cancellationToken);

            result.Category = category;
            result.Keywords = keywords;
            result.Description = description;

            // Write metadata to file
            var writeSuccess = await _imageMetadataService.WriteMetadataAsync(
                item.FilePath,
                category,
                keywords,
                description);

            if (!writeSuccess)
            {
                result.Success = false;
                result.Error = "Failed to write metadata";
                return result;
            }

            // Update in data source if applicable (e.g., Daminion)
            if (_session.DataSource.Type == DataSourceType.Daminion)
            {
                await _dataSourceProvider.UpdateItemMetadataAsync(
                    item.Id,
                    category,
                    keywords,
                    description,
                    cancellationToken);
            }

            result.Success = true;
            Log($"✓ Processed: {Path.GetFileName(item.FilePath)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing item {ItemId}", item.Id);
            result.Success = false;
            result.Error = ex.Message;
            Log($"✗ Failed: {Path.GetFileName(item.FilePath)} - {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            result.ProcessingDuration = stopwatch.Elapsed;
        }

        return result;
    }

    private void Log(string message)
    {
        _logger.LogInformation(message);
        LogMessage?.Invoke(this, message);
    }

    private void ReportProgress(int current, int total)
    {
        var percentage = (double)current / total * 100;
        ProgressChanged?.Invoke(this, new ProgressEventArgs
        {
            Current = current,
            Total = total,
            Percentage = percentage
        });
    }
}

public class ProgressEventArgs : EventArgs
{
    public int Current { get; set; }
    public int Total { get; set; }
    public double Percentage { get; set; }
}
