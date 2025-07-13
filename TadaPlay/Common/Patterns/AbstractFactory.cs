namespace TadaPlay.Common.Patterns;

public interface IFactory<T>
{
    T CreateInstance(params object[] args);
}

public abstract class AbstractFactory<T> : IFactory<T>
{

    private static readonly Dictionary<System.Type, Func<object[], T>> creators = new();


    protected static void RegisterType<U>(Func<object[], U> creator) where U : T
    {
        creators[typeof(U)] = args => creator(args);
    }


    public virtual T CreateInstance(params object[] args)
    {
        if (creators.TryGetValue(typeof(T), out var creator))
        {
            return creator(args);
        }
        throw new InvalidOperationException($"No registered creator for type {typeof(T)}");
    }
}