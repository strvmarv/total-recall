namespace TotalRecall.Web;

/// <summary>Launch options for the local web UI server.</summary>
/// <param name="Port">TCP port; 0 = pick a free ephemeral port.</param>
/// <param name="Host">Bind host; default 127.0.0.1 (loopback only).</param>
/// <param name="OpenBrowser">Launch the default browser at the served URL.</param>
/// <param name="Token">Bearer token required on /api/*; empty => generate one.</param>
/// <param name="Smoke">Boot, confirm health, then exit (CI hook).</param>
public sealed record WebUiOptions(
    int Port,
    string Host,
    bool OpenBrowser,
    string Token,
    bool Smoke);
