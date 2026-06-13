using System;
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

        // --- Embedded static files (placeholder page in Plan 1) ---
        var embedded = new ManifestEmbeddedFileProvider(
            typeof(WebUiServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });

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
}
