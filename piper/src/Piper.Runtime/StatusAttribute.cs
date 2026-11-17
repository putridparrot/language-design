// In Piper.Web's namespace, where programs have always named it (`using Piper.Web;`), but in Piper.Runtime, so code a
// client shares with its server (an error enum, through piper.json's "projects") needs no ASP.NET Core. Piper.Web
// forwards the type here, so assemblies built against it there still find it.
namespace Piper.Web;

/// <summary>
/// <c>[Status(409)]</c> on an enum variant: the HTTP status a handler's <c>Err</c> holding it answers. On a whole enum
/// (or a record), the status of all its errors, which a variant's own overrides. Without one, an error is a 400.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class StatusAttribute(int code) : Attribute
{
    public int Code { get; } = code;
}
