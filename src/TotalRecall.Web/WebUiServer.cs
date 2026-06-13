using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using TotalRecall.Server;
using TotalRecall.Web.Api;
using TotalRecall.Web.Security;

namespace TotalRecall.Web;

public static partial class WebUiServer
{
    public const string TokenHeader = "X-Total-Recall-Token";

    /// <summary>
    /// Builds (but does not start) the configured web app. Testable seam:
    /// callers inject the ToolRegistry, the expected token, and display labels.
    /// </summary>
    public static WebApplication BuildApp(
        WebUiOptions options,
        ToolRegistry registry,
        string token,
        string backendLabel,
        string version)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, WebJsonContext.Default));

        var app = builder.Build();

        // --- Security middleware: Host-header allowlist + token on /api/* ---
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;

            if (!LocalAuth.IsAllowedHost(ctx.Request.Headers.Host.ToString(), options.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(
                    new ApiError("forbidden_host", "Host header not allowed."),
                    WebJsonContext.Default.ApiError);
                return;
            }

            // /api/health is open (liveness/smoke); everything else under /api needs the token.
            if (path.StartsWithSegments("/api") && !path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            {
                var provided = ctx.Request.Headers[TokenHeader].ToString();
                if (!LocalAuth.TokenMatches(token, string.IsNullOrEmpty(provided) ? null : provided))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(
                        new ApiError("unauthorized", "Missing or invalid token."),
                        WebJsonContext.Default.ApiError);
                    return;
                }
            }

            await next();
        });

        // --- Embedded static assets (JS/CSS/etc). index.html is NOT served here;
        //     it is returned by the SPA fallback below with the bootstrap injected. ---
        var embedded = new ManifestEmbeddedFileProvider(
            typeof(WebUiServer).Assembly, "wwwroot");
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });

        // Build the bootstrapped shell HTML once (token/backend/version are fixed per launch).
        var indexHtml = BuildIndexHtml(embedded, token, backendLabel, version);

        // --- API ---
        app.MapGet("/api/health", () => Results.Json(
            new HealthInfo("ok", backendLabel, version), WebJsonContext.Default.HealthInfo));

        app.MapPost("/api/tool/{name}", async (string name, HttpRequest req, CancellationToken ct) =>
        {
            if (!ToolAllowlist.IsAllowed(name) || !registry.TryGet(name, out var handler) || handler is null)
                return Results.Json(
                    new ApiError("unknown_tool", $"Tool '{name}' is not available."),
                    WebJsonContext.Default.ApiError,
                    statusCode: StatusCodes.Status404NotFound);

            JsonElement? args = null;
            using (var reader = new StreamReader(req.Body, leaveOpen: false))
            {
                var body = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        args = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return Results.Json(
                            new ApiError("invalid_json", "Request body is not valid JSON."),
                            WebJsonContext.Default.ApiError,
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                }
            }

            var result = await handler.ExecuteAsync(args, ct);
            var text = result.Content.Length > 0 ? result.Content[0].Text : "null";
            var status = (result.IsError ?? false)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status200OK;
            return Results.Content(text, "application/json", System.Text.Encoding.UTF8, status);
        });

        // SPA fallback: any non-/api route returns the bootstrapped shell so
        // client-side routing (deep links / refresh) works. /api/* unmatched -> 404 JSON.
        app.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(
                    new ApiError("not_found", "No such API route."),
                    WebJsonContext.Default.ApiError);
                return;
            }
            // Non-/api unmatched route -> SPA shell. A missing static asset also lands here
            // and returns the shell with 200; harmless because the built index.html only
            // references hashed assets that exist.
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsync(indexHtml);
        });

        return app;
    }

    /// <summary>Reads the actual bound URL after StartAsync (resolves port 0).</summary>
    public static string ResolveBoundUrl(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var addr = feature?.Addresses.FirstOrDefault();
        if (string.IsNullOrEmpty(addr))
            throw new InvalidOperationException(
                "Web server reported no bound address; Kestrel may have failed to start.");
        return addr;
    }

    /// <summary>
    /// Reads the embedded index.html and injects
    /// <c>window.__TR_BOOTSTRAP__ = {token, backend, version}</c> before
    /// &lt;/head&gt; so the SPA can authenticate /api/* and show the backend.
    /// </summary>
    private static string BuildIndexHtml(
        Microsoft.Extensions.FileProviders.IFileProvider embedded,
        string token, string backend, string version)
    {
        var file = embedded.GetFileInfo("index.html");
        string html;
        if (file.Exists)
        {
            using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            html = reader.ReadToEnd();
        }
        else
        {
            html = "<!doctype html><html><head></head><body>"
                 + "<p>total-recall web UI assets missing.</p></body></html>";
        }

        var json = JsonSerializer.Serialize(
            new BootstrapInfo(token, backend, version),
            WebJsonContext.Default.BootstrapInfo);
        var script = $"<script>window.__TR_BOOTSTRAP__ = {json};</script>";

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headClose >= 0 ? html.Insert(headClose, script) : script + html;
    }
}
