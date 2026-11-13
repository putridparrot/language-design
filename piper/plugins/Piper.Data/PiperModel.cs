using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// What a <c>database</c> declaration's DbContext calls so EF Core stores Piper's types: an <c>Option&lt;T&gt;</c>
/// property is a nullable column, None being NULL. EF Core never passes NULL to a converter; it leaves the property's
/// default, which for an Option is None.
/// </summary>
public static class PiperModel
{
    /// <summary>From <c>OnConfiguring</c>: queries comparing Options translate (see <see cref="PiperQueries"/>).</summary>
    public static void Configuring(DbContextOptionsBuilder options) =>
        options.AddInterceptors(PiperQueries.Instance)
            .ReplaceService<Microsoft.EntityFrameworkCore.Query.IEvaluatableExpressionFilter, PiperEvaluatableFilter>();

    /// <summary>
    /// From <c>ConfigureConventions</c>: the converters, for Options of the types EF Core stores, and for the Piper enums
    /// without fields that <paramref name="context"/>'s tables use, stored as their variants' names.
    /// </summary>
    public static void Conventions(ModelConfigurationBuilder configuration, DbContext context)
    {
        // A Piper enum is a C# record hierarchy, which EF Core would take for entities: say they're values.
        foreach (var type in ColumnTypes(context.GetType()).Where(PiperEnumConverter.IsFieldlessPiperEnum))
        {
            configuration.Properties(type).HaveConversion(typeof(PiperEnumConverter<>).MakeGenericType(type));
            configuration.Properties(typeof(Option<>).MakeGenericType(type)).HaveConversion(typeof(OptionPiperEnumConverter<>).MakeGenericType(type));
        }
        // A newtype is stored as its value: an IssueId column is an int column.
        foreach (var type in ColumnTypes(context.GetType()))
        {
            if (PiperNewtype.ValueType(type) is not { } value)
                continue;
            configuration.Properties(type).HaveConversion(typeof(PiperNewtypeConverter<,>).MakeGenericType(type, value));
            var optional = value.IsValueType ? typeof(OptionNewtypeValueConverter<,>) : typeof(OptionNewtypeReferenceConverter<,>);
            configuration.Properties(typeof(Option<>).MakeGenericType(type)).HaveConversion(optional.MakeGenericType(type, value));
        }
        Conventions(configuration);
    }

    /// <summary>The types of the properties of <paramref name="context"/>'s tables' types, Options' as the type they hold.</summary>
    private static IEnumerable<Type> ColumnTypes(Type context) =>
        context.GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .SelectMany(entity => entity.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.PropertyType)
            .Select(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>) ? t.GetGenericArguments()[0] : t)
            .Distinct();

    /// <summary>The Option converters alone.</summary>
    public static void Conventions(ModelConfigurationBuilder configuration)
    {
        Reference<string>(configuration);
        Reference<byte[]>(configuration);
        Value<int>(configuration);
        Value<long>(configuration);
        Value<short>(configuration);
        Value<byte>(configuration);
        Value<bool>(configuration);
        Value<double>(configuration);
        Value<float>(configuration);
        Value<decimal>(configuration);
        Value<Guid>(configuration);
        Value<DateTime>(configuration);
        Value<DateTimeOffset>(configuration);
        Value<DateOnly>(configuration);
        Value<TimeOnly>(configuration);
        Value<TimeSpan>(configuration);
    }

    /// <summary>
    /// From <c>OnModelCreating</c>: each Option property's column may be NULL. EF Core makes a column nullable only when
    /// the property's .NET type is (a reference type or <c>Nullable&lt;T&gt;</c>), and refuses to be told otherwise
    /// (<c>IsNullable = true</c> throws), as it can't know that a struct has a value meaning "nothing". An Option does:
    /// None. So its flag is set directly. EF Core then reads NULL as the property's default, None, and writes None,
    /// which the converter makes null, as NULL. EF Core's version is the plugin's own, so this is checked by its tests.
    /// </summary>
    public static void Configure(ModelBuilder model)
    {
        // A `let` field is a get-only property, which EF Core doesn't map on its own: map it, through its backing
        // field. Computed properties (`prop Total => decimal = …`) have no backing field, and stay unmapped.
        foreach (var entity in model.Model.GetEntityTypes().ToList())
        {
            foreach (var property in entity.ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property is { CanRead: true, CanWrite: false } && entity.FindProperty(property.Name) is null &&
                    entity.ClrType.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
                    model.Model.FindEntityType(property.PropertyType) is null)
                {
                    model.Entity(entity.ClrType).Property(property.Name);
                }
            }
        }

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType.IsGenericType && property.ClrType.GetGenericTypeDefinition() == typeof(Option<>))
                    MakeNullable(property);
            }
            // EF Core numbers new rows itself for an int or long key, but not for a newtype of one, unless told to.
            if (entity.FindPrimaryKey() is { Properties: [var key] } && PiperNewtype.ValueType(key.ClrType) is { } value &&
                (value == typeof(int) || value == typeof(long)) && key.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never)
            {
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
            }
        }
    }

    private static void MakeNullable(Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property)
    {
        var field = property.GetType().GetField("_isNullable", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"This version of EF Core can't store '{property.DeclaringType.DisplayName()}.{property.Name}', an Option, as a nullable column.");
        field.SetValue(property, (bool?)true);
    }

    private static void Reference<T>(ModelConfigurationBuilder configuration) where T : class =>
        configuration.Properties<Option<T>>().HaveConversion<OptionReferenceConverter<T>>();

    private static void Value<T>(ModelConfigurationBuilder configuration) where T : struct =>
        configuration.Properties<Option<T>>().HaveConversion<OptionValueConverter<T>>();
}

/// <summary>Recognises the Piper enums that can be a column: those whose variants have no fields.</summary>
public static class PiperEnumConverter
{
    /// <summary>
    /// A Piper enum (an abstract record, which the compiler marks for JSON) whose variants, records nested in it, all
    /// have no fields: <c>enum Priority { Low, Normal, High }</c>.
    /// </summary>
    public static bool IsFieldlessPiperEnum(Type type) =>
        type is { IsAbstract: true, IsClass: true } &&
        type.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>()?.ConverterType == typeof(PiperEnumJsonConverterFactory) &&
        Variants(type) is { Count: > 0 } variants && variants.Values.All(v => v.GetConstructors().All(c => c.GetParameters().Length == 0));

    internal static Dictionary<string, Type> Variants(Type type) =>
        type.GetNestedTypes(BindingFlags.Public).Where(nested => nested.BaseType == type).ToDictionary(nested => nested.Name);
}

/// <summary>
/// A Piper enum without fields, stored as its variant's name: <c>Priority.High</c> is <c>'High'</c>, which reads well
/// in the database and survives reordering the variants.
/// </summary>
public sealed class PiperEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => value.ToString()!,
    name => Parse(name))
    where TEnum : class
{
    private static readonly Dictionary<string, TEnum> Values =
        PiperEnumConverter.Variants(typeof(TEnum)).ToDictionary(v => v.Key, v => (TEnum)Activator.CreateInstance(v.Value)!);

    internal static TEnum Parse(string name) =>
        Values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"'{name}', stored in the database, isn't a {typeof(TEnum).Name}.");
}

/// <summary>An optional Piper enum without fields: the variant's name, or NULL for None.</summary>
public sealed class OptionPiperEnumConverter<TEnum>() : ValueConverter<Option<TEnum>, string?>(
    option => option.IsSome ? option.Value.ToString() : null,
    name => name == null ? Option<TEnum>.None : Option<TEnum>.Some(PiperEnumConverter<TEnum>.Parse(name)))
    where TEnum : class;

/// <summary>An <c>Option&lt;T&gt;</c> of a reference type, stored as the value or NULL.</summary>
public sealed class OptionReferenceConverter<T>() : ValueConverter<Option<T>, T?>(
    option => option.IsSome ? option.Value : null,
    value => value == null ? Option<T>.None : Option<T>.Some(value))
    where T : class;

/// <summary>A newtype, stored as its value: <c>IssueId(5)</c> is <c>5</c>. Reading checks its invariants.</summary>
public sealed class PiperNewtypeConverter<TNewtype, TValue>() : ValueConverter<TNewtype, TValue>(
    newtype => PiperNewtype<TNewtype, TValue>.Value(newtype),
    value => PiperNewtype<TNewtype, TValue>.Make(value))
    where TNewtype : struct;

/// <summary>An optional newtype of a value type, stored as its value or NULL.</summary>
public sealed class OptionNewtypeValueConverter<TNewtype, TValue>() : ValueConverter<Option<TNewtype>, TValue?>(
    option => option.IsSome ? PiperNewtype<TNewtype, TValue>.Value(option.Value) : null,
    value => value.HasValue ? Option<TNewtype>.Some(PiperNewtype<TNewtype, TValue>.Make(value.Value)) : Option<TNewtype>.None)
    where TNewtype : struct
    where TValue : struct;

/// <summary>An optional newtype of a reference type (a string), stored as its value or NULL.</summary>
public sealed class OptionNewtypeReferenceConverter<TNewtype, TValue>() : ValueConverter<Option<TNewtype>, TValue?>(
    option => option.IsSome ? PiperNewtype<TNewtype, TValue>.Value(option.Value) : null,
    value => value != null ? Option<TNewtype>.Some(PiperNewtype<TNewtype, TValue>.Make(value)) : Option<TNewtype>.None)
    where TNewtype : struct
    where TValue : class;

/// <summary>An <c>Option&lt;T&gt;</c> of a value type, stored as the value or NULL.</summary>
public sealed class OptionValueConverter<T>() : ValueConverter<Option<T>, T?>(
    option => option.IsSome ? option.Value : null,
    value => value.HasValue ? Option<T>.Some(value.Value) : Option<T>.None)
    where T : struct;
