using System;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Synapic.UI.Commands;

/// <summary>
/// Simple relay command implementation for MVVM pattern
/// 
/// This class implements the ICommand interface to allow binding from view models.
/// It supports both synchronous and asynchronous command execution.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    
    /// <summary>
    /// Creates a new relay command
    /// </summary>
    /// <param name="execute">The action to execute when the command is invoked</param>
    /// <param name="canExecute">Optional function to determine if the command can execute</param>
    /// <exception cref="ArgumentNullException">Thrown when execute is null</exception>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }
    
    /// <summary>
    /// Occurs when changes occur that affect whether the command should execute
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    
    /// <summary>
    /// Determines if the command can execute in its current state
    /// </summary>
    /// <param name="parameter">Command parameter (optional)</param>
    /// <returns>True if the command can execute, false otherwise</returns>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    
    /// <summary>
    /// Executes the command
    /// </summary>
    /// <param name="parameter">Command parameter (optional)</param>
    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>
/// Async relay command implementation for MVVM pattern
/// 
/// This class implements ICommand for asynchronous operations and prevents
/// concurrent execution by tracking execution state.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;
    
    /// <summary>
    /// Creates a new async relay command
    /// </summary>
    /// <param name="execute">The async action to execute when the command is invoked</param>
    /// <param name="canExecute">Optional function to determine if the command can execute</param>
    /// <exception cref="ArgumentNullException">Thrown when execute is null</exception>
    public AsyncRelayCommand(Func<Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }
    
    /// <summary>
    /// Occurs when changes occur that affect whether the command should execute
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    
    /// <summary>
    /// Determines if the command can execute in its current state
    /// </summary>
    /// <param name="parameter">Command parameter (optional)</param>
    /// <returns>True if the command can execute, false if already executing</returns>
    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
    }
    
    /// <summary>
    /// Executes the command asynchronously
    /// </summary>
    /// <param name="parameter">Command parameter (optional)</param>
    /// <returns>Task that completes when execution finishes</returns>
    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _isExecuting = true;
            try
            {
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                // Requery can execute to update UI state
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}