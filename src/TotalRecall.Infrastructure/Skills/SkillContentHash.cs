using System.Security.Cryptography;
using System.Text;

namespace TotalRecall.Infrastructure.Skills;

public static class SkillContentHash
{
    public static string Compute(string content)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
