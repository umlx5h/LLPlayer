using System;
using System.Windows.Input;

namespace FlyleafLib.Controls.WPF;

public class RelayCommand : ICommand
{
    private Action<object> execute;

    private Predicate<object> canExecute;

    private event EventHandler CanExecuteChangedInternal;

    public RelayCommand(Action<object> execute) : this(execute, DefaultCanExecute) { }

    public RelayCommand(Action<object> execute, Predicate<object> canExecute)
    {
        this.execute    = execute ?? throw new ArgumentNullException("execute");
        this.canExecute = canExecute ?? throw new ArgumentNullException("canExecute");
    }

    public event EventHandler CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            CanExecuteChangedInternal += value;
        }

        remove
        {
            CommandManager.RequerySuggested -= value;
            CanExecuteChangedInternal -= value;
        }
    }

    private static bool DefaultCanExecute(object parameter) => true;
    public bool CanExecute(object parameter) => canExecute != null && canExecute(parameter);

    public void Execute(object parameter) => execute(parameter);

    public void OnCanExecuteChanged()
    {
        var handler = CanExecuteChangedInternal;
        handler?.Invoke(this, EventArgs.Empty);
         //CommandManager.InvalidateRequerySuggested();
    }

    public void Destroy()
    {
        canExecute = _ => false;
        execute = _ => { return; };
    }
}
