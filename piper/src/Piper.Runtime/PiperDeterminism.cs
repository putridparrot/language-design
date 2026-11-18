namespace Piper.Runtime;

/// <summary>
/// On a function: its code must do the same every time it runs on the same inputs, as a workflow's must, since it's
/// replayed from its history (the workflows plugin puts it on a workflow's body). The compiler follows what it calls, and
/// reports (PIP0513) what could differ between runs: the clock, random numbers, new GUIDs, threads, blocking, I/O. What's
/// passed to a <see cref="PiperEffectsAttribute"/> parameter (an activity) is left out, as it runs once and is recorded.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PiperDeterministicAttribute : Attribute;

/// <summary>
/// On a delegate parameter: the function given may have effects (read the clock, do I/O), as it's run once and its result
/// recorded, not replayed: a workflow's activity. Code a <see cref="PiperDeterministicAttribute"/> function passes here
/// isn't checked.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PiperEffectsAttribute : Attribute;
