using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using TotalRecall.Server;
using TotalRecall.Web.Security;

namespace TotalRecall.Web;

public static partial class WebUiServer
{
    /// <summary>
    /// Production entry: open the composition the MCP server uses, build the
    /// app, start it, optionally open the browser, and block until cancelled.
    /// In Smoke mode, confirm /api/health locally and return 0 immediately.
    /// </summary>
    public static async Task<int> RunAsync(WebUiOptions options, CancellationToken ct)
    {
        var token = string.IsNullOrEmpty(options.Token) ? LocalAuth.GenerateToken() : options.Token;

        ServerCompositionHandles handles;
        try
        {
            handles = ServerComposition.OpenProduction();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall ui: failed to open composition: {ex.Message}");
            return 1;
        }

        try
        {
            var info = typeof(WebUiServer).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = "unknown";
            if (!string.IsNullOrEmpty(info))
            {
                // Strip the SourceLink +<sha> suffix for human-facing output (mirrors CliApp.ResolveAppVersion).
                var plus = info.IndexOf('+');
                version = plus >= 0 ? info[..plus] : info;
            }
            var app = BuildApp(options, handles.Registry, token, handles.StorageMode, version);

            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall ui: failed to start: {ex.Message}");
                return 1;
            }
            var url = ResolveBoundUrl(app);
            Console.WriteLine($"total-recall ui: {url}");
            Console.WriteLine($"total-recall ui: token {token}");

            if (options.Smoke)
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bool ok;
                try
                {
                    var resp = await probe.GetAsync($"{url}/api/health", ct);
                    ok = resp.IsSuccessStatusCode;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"total-recall ui: smoke probe failed: {ex.Message}");
                    ok = false;
                }
                // Explicit shutdown uses None, not the caller's ct: a cancelled ct
                // would collapse the graceful-shutdown grace period to zero.
                await app.StopAsync(CancellationToken.None);
                return ok ? 0 : 1;
            }

            if (options.OpenBrowser) TryOpenBrowser(url);

            await app.WaitForShutdownAsync(ct);
            return 0;
        }
        finally
        {
            handles.Dispose();
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch
        {
            // Best-effort; the URL is already printed to the console.
        }
    }
}
