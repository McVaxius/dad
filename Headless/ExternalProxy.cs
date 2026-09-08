using System.Reflection;

namespace dad.Headless;

public class ExternalProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[], object?> Call { get; set; } = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => Call(method!, args ?? []);

    internal static T Create<T>(Func<MethodInfo, object?[], object?> call) where T : class
        => (T)Create(typeof(T), call);

    internal static object Create(Type contract, Func<MethodInfo, object?[], object?> call)
    {
        var proxy = Create(contract, typeof(ExternalProxy));
        ((ExternalProxy)proxy).Call = call;
        return proxy;
    }
}
