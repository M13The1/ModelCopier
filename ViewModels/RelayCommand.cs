using System;
using System.Windows.Input;

namespace ModelCopier.ViewModels
{
    /// <summary>
    /// A simple <see cref="ICommand"/> implementation backed by delegates.
    /// Hooks into <see cref="CommandManager.RequerySuggested"/> so WPF
    /// re-evaluates CanExecute automatically on UI events.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        /// <summary>Forces WPF to re-query CanExecute for all commands.</summary>
        public static void Refresh() => CommandManager.InvalidateRequerySuggested();
    }
}
