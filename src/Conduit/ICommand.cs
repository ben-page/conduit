namespace Conduit;

public interface ICommand
{
    void Validate() { }
}

public interface ICommand<TResult> : ICommand;
