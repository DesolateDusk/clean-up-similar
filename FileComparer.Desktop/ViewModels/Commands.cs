using System.Windows.Input;

namespace FileComparer.Desktop.ViewModels;

public sealed class RelayCommand(
    Action execute,
    Func<bool>? canExecute = null)
    : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null)
    : ICommand
{
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T>(
    Func<T, Task> execute,
    Predicate<T>? canExecute = null)
    : ICommand
    where T : class
{
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !isExecuting &&
        parameter is T value &&
        (canExecute?.Invoke(value) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || parameter is not T value)
        {
            return;
        }

        isExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute(value);
        }
        finally
        {
            isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
