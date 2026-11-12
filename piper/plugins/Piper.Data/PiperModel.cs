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

    /// <summary>From <c>ConfigureConventions</c>: the converters, for Options of the types EF Core stores.</summary>
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
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType.IsGenericType && property.ClrType.GetGenericTypeDefinition() == typeof(Option<>))
                    MakeNullable(property);
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

/// <summary>An <c>Option&lt;T&gt;</c> of a reference type, stored as the value or NULL.</summary>
public sealed class OptionReferenceConverter<T>() : ValueConverter<Option<T>, T?>(
    option => option.IsSome ? option.Value : null,
    value => value == null ? Option<T>.None : Option<T>.Some(value))
    where T : class;

/// <summary>An <c>Option&lt;T&gt;</c> of a value type, stored as the value or NULL.</summary>
public sealed class OptionValueConverter<T>() : ValueConverter<Option<T>, T?>(
    option => option.IsSome ? option.Value : null,
    value => value.HasValue ? Option<T>.Some(value.Value) : Option<T>.None)
    where T : struct;
