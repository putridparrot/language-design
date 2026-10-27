using System.Collections.Concurrent;
using System.Reflection;

namespace Piper.Binding;

/// <summary>
/// C# 11's <c>required</c> members of .NET types: properties and fields that whoever makes an object must set in its
/// object initializer, unless the constructor used is marked <c>[SetsRequiredMembers]</c>. Attributes are matched by
/// name, as a referenced assembly's may come from its own load context.
/// </summary>
public static class RequiredMembers
{
    private const string RequiredMember = "System.Runtime.CompilerServices.RequiredMemberAttribute";
    private const string SetsRequired = "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute";

    private static readonly ConcurrentDictionary<Type, string[]> Cache = new();

    /// <summary>The names of <paramref name="type"/>'s required members, its own and inherited, in declaration order.</summary>
    public static string[] Of(Type type) => Cache.GetOrAdd(type, t =>
        [.. t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m is PropertyInfo or FieldInfo && Has(m, RequiredMember))
            .Select(m => m.Name)
            .Distinct()]);

    /// <summary>Whether <paramref name="constructor"/> sets every required member itself.</summary>
    public static bool AreSetBy(ConstructorInfo constructor) => Has(constructor, SetsRequired);

    /// <summary>
    /// Whether <paramref name="member"/>'s <c>[Obsolete]</c> is only C#'s guard against compilers that don't know
    /// required members: it marks the constructors of a type with them so, with <c>[CompilerFeatureRequired("RequiredMembers")]</c>.
    /// </summary>
    public static bool IsObsoleteOnlyForOlderCompilers(MemberInfo member) =>
        member is ConstructorInfo && member.CustomAttributes.Any(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute" &&
            a.ConstructorArguments is [{ Value: "RequiredMembers" }]);

    private static bool Has(MemberInfo member, string attribute) =>
        member.CustomAttributes.Any(a => a.AttributeType.FullName == attribute);
}
