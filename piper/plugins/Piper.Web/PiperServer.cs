using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Piper.Web;

/// <summary>
/// What the code a <c>server</c> declaration writes calls: where the app listens, and HTTPS. Its workers, stopping
/// hooks, shutdown timeout and logging are Piper.Hosting's (<c>PiperHost</c>), as a <c>host</c>'s are.
/// </summary>
public static class PiperServer
{
    /// <summary><c>urls "http://localhost:5080";</c></summary>
    public static void Urls(WebApplicationBuilder builder, string url) => builder.WebHost.UseUrls(url);

    /// <summary><c>urls ["http://localhost:5080", "https://localhost:5081"];</c></summary>
    public static void Urls(WebApplicationBuilder builder, IEnumerable<string> urls) => builder.WebHost.UseUrls([.. urls]);

    /// <summary>
    /// <c>forwarded;</c>: behind a proxy that ends TLS (a Kubernetes ingress, a load balancer), a request's scheme and
    /// client address are the ones the proxy saw, from its <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c>, so
    /// <c>https</c> doesn't redirect a request that arrived as HTTPS, and links and logs show the client. With
    /// <paramref name="trusted"/> addresses (<c>"10.1.2.3"</c>) or networks (<c>"10.0.0.0/8"</c>), only requests from
    /// them are believed; without, any are, as ASP.NET Core's <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> does, which suits
    /// an app only the proxy can reach.
    /// </summary>
    public static void Forwarded(WebApplicationBuilder builder, params string[] trusted)
    {
        var proxies = new List<System.Net.IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in trusted)
        {
            if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
                networks.Add(network);
            else if (System.Net.IPAddress.TryParse(entry, out var address))
                proxies.Add(address);
            else
                throw new InvalidOperationException($"'{entry}' isn't an IP address (10.1.2.3) or a network (10.0.0.0/8), so it can't be a trusted proxy.");
        }
        builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            // ASP.NET Core trusts only loopback by default, which a proxy in another pod or machine never is.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
            foreach (var proxy in proxies)
                options.KnownProxies.Add(proxy);
        });
    }

    /// <summary>
    /// <c>https;</c>: HTTP requests are redirected to HTTPS, and outside Development, browsers are told to use only
    /// HTTPS from then on (HSTS). Served from memory by a TestClient, which has no HTTPS address, nothing is redirected.
    /// </summary>
    public static void Https(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.UseHttpsRedirection();
    }

    /// <summary><c>https certificate "server.pfx";</c>: the certificate HTTPS serves, from a PKCS#12 (.pfx) file.</summary>
    public static void Certificate(WebApplicationBuilder builder, string path) =>
        UseCertificate(builder, path, () => X509CertificateLoader.LoadPkcs12FromFile(path, null));

    /// <summary><c>https certificate tls.path password tls.password;</c>: from a PKCS#12 (.pfx) file with a password.</summary>
    public static void Certificate(WebApplicationBuilder builder, string path, string? password) =>
        UseCertificate(builder, path, () => X509CertificateLoader.LoadPkcs12FromFile(path, password));

    /// <summary>
    /// <c>https certificate "/tls/tls.crt" key "/tls/tls.key";</c>: from PEM files, as a Kubernetes TLS secret
    /// mounts them.
    /// </summary>
    public static void PemCertificate(WebApplicationBuilder builder, string certificate, string key) =>
        UseCertificate(builder, certificate, () =>
        {
            var pem = X509Certificate2.CreateFromPemFile(certificate, key);
            if (!OperatingSystem.IsWindows())
                return pem;
            // Windows' TLS can't use a key held only in memory, as a PEM file's is; through PKCS#12 it's one it can.
            using (pem)
                return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        });

    /// <summary>
    /// Every HTTPS address serves the certificate, loaded when the server first binds one, so an app served from memory
    /// (a test's) needs no file, and a missing or unreadable one stops the app as it starts, saying which.
    /// </summary>
    private static void UseCertificate(WebApplicationBuilder builder, string path, Func<X509Certificate2> load)
    {
        var certificate = new Lazy<X509Certificate2>(() =>
        {
            try
            {
                return load();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new InvalidOperationException($"The server's certificate couldn't be loaded from '{path}': {e.Message}", e);
            }
        });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate.Value));
    }
}

/// <summary>
/// What a <c>server</c>'s <c>health "/healthz" ready "/ready" { check … }</c> writes calls: ASP.NET Core's health
/// checks, with checks written as handlers.
/// </summary>
public static class PiperHealth
{
    private const string ReadyTag = "piper-ready";

    /// <summary>Registers the health check service, which the endpoints need even with no checks.</summary>
    public static void Add(IServiceCollection services) => services.AddHealthChecks();

    /// <summary>
    /// <c>check "database" (db: Db) =&gt; await db.Database.CanConnectAsync();</c>: healthy when it gives true (or a
    /// <see cref="HealthCheckResult"/> saying so), unhealthy when it gives false or throws.
    /// </summary>
    public static void AddCheck(IServiceCollection services, string name, Delegate check) =>
        services.AddHealthChecks().Add(new HealthCheckRegistration(name, provider => new HandlerCheck(provider, check), null, [ReadyTag]));

    /// <summary>
    /// Maps the health endpoints. With only <paramref name="health"/>, it runs every check. With
    /// <paramref name="ready"/> too, <paramref name="health"/> is liveness, healthy while the app runs (so Kubernetes
    /// doesn't restart it when a database is down), and <paramref name="ready"/> runs the checks (so it's sent no
    /// requests until they pass).
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, string health, string? ready)
    {
        if (ready is null)
        {
            app.MapHealthChecks(health);
            return;
        }
        app.MapHealthChecks(health, new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(ready, new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(ReadyTag) });
    }

    private sealed class HandlerCheck(IServiceProvider services, Delegate check) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                return await Piper.Hosting.PiperHost.Invoke(services, check, cancellationToken) switch
                {
                    HealthCheckResult result => result,
                    false => HealthCheckResult.Unhealthy(),
                    _ => HealthCheckResult.Healthy(),
                };
            }
            catch (Exception e)
            {
                return HealthCheckResult.Unhealthy(e.Message, e);
            }
        }
    }
}
