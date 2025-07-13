namespace TadaPlay.Common.Patterns;

public abstract class SingletonBase<T> where T : SingletonBase<T>, new()
{

    private static readonly Lazy<T> instance = new Lazy<T>(() => new T());

    public static T Instance => instance.Value;

    protected SingletonBase()
    {
        if (instance.IsValueCreated)
        {
            throw new InvalidOperationException("The instance has been created!");
        }
    }

    protected virtual void Initialize()
    {
        // 
    }
}