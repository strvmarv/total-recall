using System.Text;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// A mebibyte of near-misses of the secret detector's own patterns (git sync spec D9): every piece starts the way a
/// token or a private-key line starts and then falls short, so each pattern is tried at thousands of places and never
/// matches. One match call over it runs long enough to pass a 1-tick timeout even where the clock is coarse.
/// </summary>
internal static class SecretScanNearMisses
{
    private const int OneMiB = 1024 * 1024;

    private const string Letters = "abcdefghijklmno";
    private const string Capitals = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMN";

    /// <summary>The near-miss pieces repeated to (at least) one mebibyte; it holds no token and no private-key line.</summary>
    public static string Text()
    {
        var unit = "sk-" + Letters + " "
                   + "ghp_" + Letters + " "
                   + "github_pat_" + Letters + " "
                   + new string('-', 5) + "BEGIN " + Capitals + " ";
        var sb = new StringBuilder(OneMiB + unit.Length);
        while (sb.Length < OneMiB) sb.Append(unit);
        return sb.ToString();
    }
}
