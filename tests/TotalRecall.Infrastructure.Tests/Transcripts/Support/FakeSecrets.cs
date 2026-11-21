using System;
using System.Text;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// Fake secrets in the shapes the redactor detects, assembled from parts at run time so that no source file or committed
/// fixture holds a secret literal. The same seed and name always give the same value.
/// </summary>
public sealed class FakeSecrets(int seed)
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Alnum = Upper + Lower + Digits;
    private const string Base64 = Alnum + "+/";

    private static readonly string Dashes = new('-', 5);

    public string Get(string name) => name switch
    {
        "github-token" => "gh" + "p_" + Pick(name, Alnum, 36),
        "github-pat" => "github" + "_pat_" + Pick(name, Alnum + "_", 59),
        "gitlab-token" => "gl" + "pat-" + Pick(name, Alnum, 20),
        "slack-token" => "xo" + "xb-" + Pick(name + "a", Digits, 10) + "-" + Pick(name + "b", Digits, 12) + "-"
                         + Pick(name + "c", Upper + Lower, 16),
        "openai-key" => "s" + "k-" + Pick(name, Alnum, 48),
        "anthropic-key" => "s" + "k-ant-api03-" + Pick(name, Alnum + "_-", 80),
        "openai-project-key" => "s" + "k-proj-" + Pick(name, Alnum + "_-", 48),
        "openai-svcacct-key" => "s" + "k-svcacct-" + Pick(name, Alnum + "_-", 48),
        "openai-admin-key" => "s" + "k-admin-" + Pick(name, Alnum + "_-", 48),
        "aws-key" => "AK" + "IA" + Pick(name, Upper + Digits, 16),
        "private-key" => PrivateKey(name, terminated: true),
        "private-key-unterminated" => PrivateKey(name, terminated: false),
        "bearer-token" => Pick(name, Alnum, 32),
        "url-password" => Pick(name + "a", Alnum, 4) + "@" + Pick(name + "b", Alnum, 4) + "/" + Pick(name + "c", Alnum, 3)
                          + "+" + Pick(name + "d", Alnum, 2),
        "api-key-value" => Pick(name + "a", Alnum, 7) + "," + Pick(name + "b", Alnum, 7) + ";" + Pick(name + "c", Alnum, 4)
                           + "&" + Pick(name + "d", Alnum, 3),
        _ => throw new ArgumentException("unknown fake secret: " + name, nameof(name)),
    };

    private string PrivateKey(string name, bool terminated)
    {
        var label = "RSA " + "PRIVATE" + " KEY";
        var sb = new StringBuilder();
        sb.Append(Dashes).Append("BEGIN ").Append(label).Append(Dashes).Append('\n');
        for (var i = 0; i < 4; i++) sb.Append(Pick(name + i, Base64, 64)).Append('\n');
        sb.Append(Pick(name + "last", Base64, 20)).Append("==");
        if (terminated) sb.Append('\n').Append(Dashes).Append("END ").Append(label).Append(Dashes);
        return sb.ToString();
    }

    private string Pick(string salt, string alphabet, int length)
    {
        var rng = new Random(StableHash(salt) ^ seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s) h = (h ^ c) * 16777619;
            return h;
        }
    }
}
