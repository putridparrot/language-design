using System.Net.Http.Json;
using System.Text.Json;

namespace Piper.Runtime;

/// <summary>
/// Why a call to an API (through an <c>api</c> declaration's client) gave no value: the HTTP status, or 0 when the server
/// couldn't be reached or its answer couldn't be read, and what it said (its ProblemDetails' <c>detail</c> or
/// <c>title</c>, or the status's reason).
/// </summary>
public class ApiError(int status, string message)
{
    public int Status { get; } = status;

    public string Message { get; } = message;

    public override string ToString() => Status == 0 ? Message : $"{Status}: {Message}";
}

/// <summary>
/// An <see cref="ApiError"/> from an endpoint whose handler answers a <c>Result&lt;T, E&gt;</c>: <see cref="Error"/> is
/// the handler's own error, read back from the ProblemDetails it was answered as, when there is one (None for any
/// other failure: the server unreachable, a 401, a 500).
/// </summary>
public sealed class ApiError<E>(int status, string message, E? error) : ApiError(status, message)
{
    public E? Error { get; } = error;

    public override string ToString() => Error is null ? base.ToString() : $"{Status}: {Error}";
}

/// <summary>
/// On each method of an <c>api</c> declaration's client: the request it sends, its HTTP method and route as the server
/// maps it (with the api's prefix). The web plugin checks <c>endpoints Name: Api</c> against these.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ApiCallAttribute(string method, string route) : Attribute
{
    public string Method { get; } = method;

    public string Route { get; } = route;
}

/// <summary>
/// What an <c>api</c> declaration's client calls: sends a request and reads its answer as the server's handler gave it
/// (see Piper.Web's <c>PiperResponses</c>): 200 with the value as JSON, 404 for an Option's None, and ProblemDetails for
/// a Result's error. Nothing here throws for a failed call; each gives an <see cref="ApiError"/>.
/// </summary>
public static class PiperClient
{
    /// <summary>JSON as ASP.NET Core writes it (camelCase, numbers from strings allowed); Piper's types bring their own converters.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Problem = "application/problem+json";

    /// <summary>A request's body, as JSON.</summary>
    public static HttpContent Body<T>(T value) => JsonContent.Create(value, options: Json);

    /// <summary>A call whose handler answers a plain value: the 200's value, or why there's none.</summary>
    public static async Task<Result<T, ApiError>> Send<T>(HttpClient http, string method, string path, HttpContent? body)
    {
        var answer = await Exchange(http, method, path, body);
        if (answer.Failure is { } failure)
            return Result<T, ApiError>.Err(failure);
        if (!answer.Success)
            return Result<T, ApiError>.Err(new ApiError(answer.Status, Said(answer)));
        var read = Read<T>(answer);
        return read.IsOk ? Result<T, ApiError>.Ok(read.Value!) : Result<T, ApiError>.Err(read.Error);
    }

    /// <summary>
    /// A call whose handler answers an Option: None for a 404. <typeparamref name="T"/> is the Option's type itself
    /// (<c>Lot?</c>, <c>int?</c>), so None is its default, whether a class or a struct.
    /// </summary>
    public static async Task<Result<T, ApiError>> SendOptional<T>(HttpClient http, string method, string path, HttpContent? body)
    {
        var answer = await Exchange(http, method, path, body);
        if (answer.Failure is { } failure)
            return Result<T, ApiError>.Err(failure);
        if (answer.Status == 404)
            return Result<T, ApiError>.Ok(default!);
        if (!answer.Success)
            return Result<T, ApiError>.Err(new ApiError(answer.Status, Said(answer)));
        var read = Read<T>(answer, allowNull: true);
        return read.IsOk ? Result<T, ApiError>.Ok(read.Value!) : Result<T, ApiError>.Err(read.Error);
    }

    /// <summary>
    /// A call whose handler answers a <c>Result&lt;T, E&gt;</c>: the 200's value, or an error holding the handler's own
    /// <c>E</c> when the server answered with one.
    /// </summary>
    public static async Task<Result<T, ApiError<E>>> SendResult<T, E>(HttpClient http, string method, string path, HttpContent? body)
    {
        var answer = await Exchange(http, method, path, body);
        if (answer.Failure is { } failure)
            return Result<T, ApiError<E>>.Err(new ApiError<E>(failure.Status, failure.Message, default));
        if (!answer.Success)
            return Result<T, ApiError<E>>.Err(new ApiError<E>(answer.Status, Said(answer), ErrorOf<E>(answer)));
        var read = Read<T>(answer);
        return read.IsOk
            ? Result<T, ApiError<E>>.Ok(read.Value!)
            : Result<T, ApiError<E>>.Err(new ApiError<E>(read.Error.Status, read.Error.Message, default));
    }

    /// <summary>A response, read: its status and body, or why there was none.</summary>
    private sealed record Answer(int Status, bool Success, string? Reason, string? MediaType, string Content, ApiError? Failure = null);

    private static async Task<Answer> Exchange(HttpClient http, string method, string path, HttpContent? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.RelativeOrAbsolute)) { Content = body };
        try
        {
            using var response = await http.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            return new Answer((int)response.StatusCode, response.IsSuccessStatusCode, response.ReasonPhrase, response.Content.Headers.ContentType?.MediaType, content);
        }
        catch (HttpRequestException e)
        {
            return new Answer(0, false, null, null, "", new ApiError(0, $"Can't reach the API: {e.Message}"));
        }
        catch (TaskCanceledException e) when (!e.CancellationToken.IsCancellationRequested || e.InnerException is TimeoutException)
        {
            return new Answer(0, false, null, null, "", new ApiError(0, "The API didn't answer in time."));
        }
    }

    /// <summary>
    /// A successful answer's value. A handler's string is written as text, not JSON (as ASP.NET Core writes it), so a
    /// <c>string</c> is read as it came. JSON <c>null</c>, or JSON that isn't a <typeparamref name="T"/>, is an error:
    /// the server and client disagree about the type.
    /// </summary>
    private static Result<T?, ApiError> Read<T>(Answer answer, bool allowNull = false)
    {
        if (typeof(T) == typeof(string) && answer.MediaType is not "application/json")
            return Result<T?, ApiError>.Ok((T)(object)answer.Content);
        try
        {
            var value = JsonSerializer.Deserialize<T>(answer.Content, Json);
            return value is not null || allowNull
                ? Result<T?, ApiError>.Ok(value)
                : Result<T?, ApiError>.Err(new ApiError(0, "The API answered with nothing."));
        }
        catch (JsonException e)
        {
            return Result<T?, ApiError>.Err(new ApiError(0, $"The API's answer isn't what this client expects: {e.Message}"));
        }
    }

    /// <summary>What a failed answer says: its ProblemDetails' detail, or title, or the status's reason.</summary>
    private static string Said(Answer answer)
    {
        if (Document(answer) is { } document)
        {
            using (document)
            {
                foreach (var name in (ReadOnlySpan<string>)["detail", "title"])
                {
                    if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString()!;
                }
            }
        }
        return answer.Reason is { Length: > 0 } reason ? reason : $"The API answered {answer.Status}.";
    }

    /// <summary>
    /// The handler's error, from the ProblemDetails it was answered as: a string is its <c>detail</c>; an enum variant
    /// or a record is the object's members (its <c>kind</c> and fields, as extensions); anything else is its
    /// <c>error</c> member. None when the answer isn't such a ProblemDetails, or isn't an <typeparamref name="E"/>.
    /// </summary>
    private static E? ErrorOf<E>(Answer answer)
    {
        if (Document(answer) is not { } document)
            return default;
        using (document)
        {
            var root = document.RootElement;
            try
            {
                if (typeof(E) == typeof(string))
                    return root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? (E)(object)detail.GetString()! : default;
                if (root.TryGetProperty("error", out var error))
                    return error.Deserialize<E>(Json);
                if (new PiperEnumJsonConverterFactory().CanConvert(typeof(E)) && !root.TryGetProperty(PiperEnumJsonConverterFactory.Kind, out _))
                    return default;
                return root.Deserialize<E>(Json);
            }
            catch (JsonException)
            {
                return default;
            }
            catch (NotSupportedException)
            {
                return default;
            }
        }
    }

    private static JsonDocument? Document(Answer answer)
    {
        if (answer.MediaType is not (Problem or "application/json") || answer.Content.Length == 0)
            return null;
        try
        {
            var document = JsonDocument.Parse(answer.Content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
