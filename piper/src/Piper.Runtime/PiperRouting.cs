using System.Diagnostics;
using System.Reflection;

namespace Piper.Runtime;

/// <summary>
/// What the compiler calls in place of .NET's clock, delays, background work, random numbers and new GUIDs, so a
/// program's own code takes them from <see cref="PiperRuntime.Current"/>: the real ones, or a simulation's
/// (<see cref="PiperSimulation"/>), in which they're simulated and repeat by seed. A program writes
/// <c>DateTime.UtcNow</c>, <c>Task.Delay(…)</c>, <c>Task.Run(…)</c>, <c>Random()</c> as usual; each member here has the
/// same parameters and result as the one it stands for (<see cref="For"/> pairs them), and outside a simulation does
/// just what it does.
/// </summary>
public static class PiperRouting
{
    // DateTime and DateTimeOffset
    public static DateTime DateTimeNow => PiperRuntime.Current.Time.GetLocalNow().DateTime;
    public static DateTime DateTimeUtcNow => PiperRuntime.Current.UtcNow;
    public static DateTime DateTimeToday => DateTimeNow.Date;
    public static DateTimeOffset DateTimeOffsetNow => PiperRuntime.Current.Time.GetLocalNow();
    public static DateTimeOffset DateTimeOffsetUtcNow => PiperRuntime.Current.Time.GetUtcNow();

    // Task.Delay
    public static Task Delay(int millisecondsDelay) => PiperRuntime.Current.Delay(TimeSpan.FromMilliseconds(millisecondsDelay));
    public static Task Delay(TimeSpan delay) => PiperRuntime.Current.Delay(delay);
    public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken) =>
        PiperRuntime.Current.Delay(TimeSpan.FromMilliseconds(millisecondsDelay), cancellationToken);
    public static Task Delay(TimeSpan delay, CancellationToken cancellationToken) => PiperRuntime.Current.Delay(delay, cancellationToken);

    // Task.Run
    public static Task Run(Action action) =>
        PiperRuntime.Current.IsSimulated ? PiperRuntime.Current.Start(() => { action(); return Task.CompletedTask; }) : Task.Run(action);
    public static Task Run(Action action, CancellationToken cancellationToken) =>
        PiperRuntime.Current.IsSimulated ? Run(action) : Task.Run(action, cancellationToken);
    public static Task Run(Func<Task?> function) =>
        PiperRuntime.Current.IsSimulated ? PiperRuntime.Current.Start(() => function() ?? Task.CompletedTask) : Task.Run(function);
    public static Task Run(Func<Task?> function, CancellationToken cancellationToken) =>
        PiperRuntime.Current.IsSimulated ? Run(function) : Task.Run(function, cancellationToken);
    public static Task<TResult> Run<TResult>(Func<TResult> function) =>
        PiperRuntime.Current.IsSimulated ? Simulated(() => Task.FromResult(function())) : Task.Run(function);
    public static Task<TResult> Run<TResult>(Func<TResult> function, CancellationToken cancellationToken) =>
        PiperRuntime.Current.IsSimulated ? Run(function) : Task.Run(function, cancellationToken);
    public static Task<TResult> Run<TResult>(Func<Task<TResult>?> function) =>
        PiperRuntime.Current.IsSimulated ? Simulated(function!) : Task.Run(function);
    public static Task<TResult> Run<TResult>(Func<Task<TResult>?> function, CancellationToken cancellationToken) =>
        PiperRuntime.Current.IsSimulated ? Run(function) : Task.Run(function, cancellationToken);

    private static async Task<TResult> Simulated<TResult>(Func<Task<TResult>> function)
    {
        TResult result = default!;
        await PiperRuntime.Current.Start(async () => result = await function());
        return result;
    }

    // Random and Guid
    public static Random RandomShared => PiperRuntime.Current.Random;
    // In a simulation, the run's own, whose numbers are its choices, so they shrink with it.
    public static Random NewRandom() => PiperRuntime.Current.IsSimulated ? PiperRuntime.Current.Random : new Random();
    public static Guid NewGuid()
    {
        if (!PiperRuntime.Current.IsSimulated)
            return Guid.NewGuid();
        Span<byte> bytes = stackalloc byte[16];
        PiperRuntime.Current.Random.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // version 4, as Guid.NewGuid's are
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    // Stopwatch's timestamps
    public static long GetTimestamp() => PiperRuntime.Current.Time.GetTimestamp();
    public static TimeSpan GetElapsedTime(long startingTimestamp) => PiperRuntime.Current.Time.GetElapsedTime(startingTimestamp);
    public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        PiperRuntime.Current.Time.GetElapsedTime(startingTimestamp, endingTimestamp);

    /// <summary>The .NET members routed here, by type and name, and their stand-ins' names.</summary>
    private static readonly Dictionary<(Type, string), string> Routes = new()
    {
        [(typeof(DateTime), nameof(DateTime.Now))] = nameof(DateTimeNow),
        [(typeof(DateTime), nameof(DateTime.UtcNow))] = nameof(DateTimeUtcNow),
        [(typeof(DateTime), nameof(DateTime.Today))] = nameof(DateTimeToday),
        [(typeof(DateTimeOffset), nameof(DateTimeOffset.Now))] = nameof(DateTimeOffsetNow),
        [(typeof(DateTimeOffset), nameof(DateTimeOffset.UtcNow))] = nameof(DateTimeOffsetUtcNow),
        [(typeof(Task), nameof(Task.Delay))] = nameof(Delay),
        [(typeof(Task), nameof(Task.Run))] = nameof(Run),
        [(typeof(Random), nameof(Random.Shared))] = nameof(RandomShared),
        [(typeof(Random), ".ctor")] = nameof(NewRandom),
        [(typeof(Guid), nameof(Guid.NewGuid))] = nameof(NewGuid),
        [(typeof(Stopwatch), nameof(Stopwatch.GetTimestamp))] = nameof(GetTimestamp),
        [(typeof(Stopwatch), nameof(Stopwatch.GetElapsedTime))] = nameof(GetElapsedTime),
    };

    /// <summary>
    /// The member here standing for the static .NET <paramref name="member"/> (a property, a method or a parameterless
    /// constructor), with the same parameters and result; null if it isn't routed (an overload taking a
    /// <c>TimeProvider</c> already, or a seeded <c>Random(seed)</c>).
    /// </summary>
    public static MemberInfo? For(MemberInfo member)
    {
        if (member.DeclaringType is not { } type || !Routes.TryGetValue((type, member.Name), out var name))
            return null;
        switch (member)
        {
            case PropertyInfo property when property.GetMethod?.IsStatic == true:
                return typeof(PiperRouting).GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { } standIn && standIn.PropertyType == property.PropertyType
                    ? standIn : null;
            case ConstructorInfo constructor when constructor.GetParameters().Length == 0:
                return typeof(PiperRouting).GetMethod(name, BindingFlags.Public | BindingFlags.Static, []);
            case MethodInfo { IsStatic: true } method:
            {
                var definition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;
                var shape = Shape(definition);
                var routed = typeof(PiperRouting).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == name && m.GetGenericArguments().Length == definition.GetGenericArguments().Length && Shape(m) == shape);
                return routed is null ? null : method.IsGenericMethod ? routed.MakeGenericMethod(method.GetGenericArguments()) : routed;
            }
            default:
                return null;
        }
    }

    /// <summary>A method's parameters and result, with generic parameters by position, so a stand-in can be matched.</summary>
    private static string Shape(MethodInfo method) =>
        string.Join(",", method.GetParameters().Select(p => Name(p.ParameterType))) + "->" + Name(method.ReturnType);

    private static string Name(Type type) =>
        type.IsGenericParameter ? $"!{type.GenericParameterPosition}"
        : type.IsGenericType ? $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", type.GetGenericArguments().Select(Name))}]"
        : type.FullName ?? type.Name;
}
