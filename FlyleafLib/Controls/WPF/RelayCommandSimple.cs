using System;
using System.Windows.Input;

namespace FlyleafLib.Controls.WPF;

public class RelayCommandSimple : ICommand
{
    public event EventHandler CanExecuteChanged { add { } remove { } }
    Action execute;

    public RelayCommandSimple(Action execute)   => this.execute = execute;
    public bool CanExecute(object parameter)    => true;
    public void Execute(object parameter)       => execute();
}
