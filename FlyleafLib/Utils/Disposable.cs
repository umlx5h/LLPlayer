namespace FlyleafLib;

/// <summary>
/// Anonymous Disposal Pattern
/// </summary>
public class Disposable : IDisposable
{
    public static Disposable Create(Action onDispose) => new(onDispose);

    public static Disposable Empty { get; } = new(null);

    Action _onDispose;
    Disposable(Action onDispose) => _onDispose = onDispose;

    public void Dispose()
    {
        _onDispose?.Invoke();
        _onDispose = null;
    }
}
