using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piper.Runtime;

namespace Piper.Web;

/// <summary>
/// A posted HTML form, read into a record: a handler takes <c>(form: Form&lt;NewLot&gt;)</c>, and matches
/// <c>form.Result</c>, <c>Ok(lot)</c> or <c>Err(errors)</c>, to save it or show the form again with what's wrong.
/// <para>
/// Each of the record's fields is read from the form field of its name, by its type: text as it is; a number, date,
/// <c>Guid</c>, enum or newtype by parsing it (numbers in the invariant culture, as browsers send them); a <c>bool</c>
/// from a checkbox (present, or "true", is true); and an optional field is None when empty. A required field that's
/// missing or empty, or text that isn't a value of its type, is an error for that field. Each of the record's own
/// <c>requires</c> is checked wherever the fields it mentions were read, so every mistake is shown at once: one about a
/// single field is that field's error, and any other the whole form's.
/// </para>
/// <para>
/// The form's anti-forgery token is checked first (every <c>&lt;form method="post"&gt;</c> in markup has one), and a
/// post without a valid one is a 400. That needs ASP.NET Core's antiforgery: <c>builder.Services.AddAntiforgery()</c>.
/// </para>
/// </summary>
public sealed class Form<T>
{
    private readonly IFormCollection _fields;

    private Form(IFormCollection fields, Result<T, FormErrors> result)
    {
        _fields = fields;
        Result = result;
    }

    /// <summary>The record, or what's wrong with the form.</summary>
    public Result<T, FormErrors> Result { get; }

    /// <summary>What was sent for <paramref name="field"/>, to show the form again as it was filled in.</summary>
    public string Text(string field) => _fields.TryGetValue(field, out var value) ? value.ToString() : "";

    /// <summary>How ASP.NET Core binds a handler's <c>Form&lt;T&gt;</c> parameter.</summary>
    public static async ValueTask<Form<T>?> BindAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
            throw new BadHttpRequestException("Expected a form.", StatusCodes.Status415UnsupportedMediaType);
        var antiforgery = context.RequestServices.GetService<IAntiforgery>()
            ?? throw new InvalidOperationException("A Form<T> checks the form's anti-forgery token, which needs ASP.NET Core's antiforgery: add builder.Services.AddAntiforgery() before building the app.");
        if (!await antiforgery.IsRequestValidAsync(context))
            throw new BadHttpRequestException("The form's anti-forgery token is missing or isn't valid: load the page again, and send the form from it.");
        var fields = await context.Request.ReadFormAsync();
        return new Form<T>(fields, PiperForms.Read<T>(fields));
    }
}

/// <summary>What's wrong with a form: messages for its fields, and for the form as a whole.</summary>
public sealed class FormErrors(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> form)
{
    /// <summary>The message for <paramref name="field"/>, if it has one: <c>{errors.For("title")}</c> beside its input.</summary>
    public string? For(string field) => fields.TryGetValue(field, out var message) ? message : null;

    /// <summary>
    /// The form's own message (the <c>requires</c> of its record that failed and are about more than one field), if
    /// it has one; several are put together, as sentences.
    /// </summary>
    public string? Form { get; } = form.Count == 0 ? null : string.Join(" ", form);

    /// <summary>Every message: the form's, then the fields'.</summary>
    public IReadOnlyList<string> All => [.. form, .. fields.Values];

    public override string ToString() => string.Join("; ", All);
}

internal static class PiperForms
{
    private static readonly ConcurrentDictionary<Type, ConstructorInfo> Constructors = new();

    public static Result<T, FormErrors> Read<T>(IFormCollection form)
    {
        var constructor = Constructors.GetOrAdd(typeof(T), type =>
            type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidOperationException($"A form is read into a record; '{type.Name}' has no constructor to make one with."));
        var parameters = constructor.GetParameters();
        var values = new object?[parameters.Length];
        var errors = new Dictionary<string, string>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var (name, type) = (parameters[i].Name!, parameters[i].ParameterType);
            var sent = form.TryGetValue(name, out var field) ? field.ToArray() : [];
            var text = sent.LastOrDefault();
            if (type == typeof(bool))
                values[i] = sent.Any(s => s is "on" or "true" or "True" or "1");
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>))
            {
                var inner = type.GetGenericArguments()[0];
                if (string.IsNullOrWhiteSpace(text))
                    values[i] = Activator.CreateInstance(type);
                else if (PiperText.TryParse(inner, text, CultureInfo.InvariantCulture, out var value))
                    values[i] = type.GetMethod(nameof(Option<int>.Some))!.Invoke(null, [value]);
                else
                    errors[name] = NotA(text, inner);
            }
            else if (text is null || (text.Trim().Length == 0 && type != typeof(string)))
            {
                if (parameters[i].HasDefaultValue)
                    values[i] = parameters[i].DefaultValue;
                else
                    errors[name] = $"{Label(name)} is required.";
            }
            else if (PiperText.TryParse(type, text, CultureInfo.InvariantCulture, out var value))
                values[i] = value;
            else
                errors[name] = NotA(text, type);
        }
        // The record's `requires`, each checked on its own wherever the fields it mentions were read, so a field that
        // isn't a number doesn't hide what's wrong with the others. One about a single field is that field's error
        // (unless it has one already); any other, the form's.
        var formErrors = new List<string>();
        foreach (var requirement in Requirements.GetOrAdd(typeof(T), FindRequirements))
        {
            if (requirement.Fields.Any(errors.ContainsKey) || requirement.Check(values) is not { } message)
                continue;
            if (requirement.Field is { } field && parameters.Any(p => p.Name == field))
                errors[field] = message;
            else
                formErrors.Add(message);
        }
        if (errors.Count > 0 || formErrors.Count > 0)
            return Result<T, FormErrors>.Err(new FormErrors(errors, formErrors));
        try
        {
            return Result<T, FormErrors>.Ok((T)constructor.Invoke(values));
        }
        // A `requires … else "message"` about one field is that field's error; any other, the form's.
        catch (TargetInvocationException e) when (e.InnerException is PiperContractException { Field: { } field } failed &&
                                                    parameters.Any(p => p.Name == field))
        {
            return Result<T, FormErrors>.Err(new FormErrors(new Dictionary<string, string> { [field] = failed.Message }, []));
        }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException invalid)
        {
            return Result<T, FormErrors>.Err(new FormErrors(errors, [invalid.Message]));
        }
    }

    private static readonly ConcurrentDictionary<Type, PiperRequirement[]> Requirements = new();

    /// <summary>The table the compiler gives a Piper record with <c>requires</c>; none for any other type.</summary>
    private static PiperRequirement[] FindRequirements(Type type) =>
        type.GetField("__PiperRequirements", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as PiperRequirement[] ?? [];

    private static string NotA(string text, Type type) => $"'{text}' isn't {Article(type.Name)} {Words(type.Name)}.";

    /// <summary><c>startPrice</c> as a person would write it: "Start price".</summary>
    private static string Label(string name)
    {
        var words = Words(name);
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Words(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString())).ToLowerInvariant() switch
        {
            "int32" or "int64" or "int16" => "whole number",
            "decimal" or "double" or "single" => "number",
            "date time" => "date",
            var words => words,
        };

    private static string Article(string name) => Words(name) is [var first, ..] && "aeiou".Contains(first) ? "an" : "a";
}
