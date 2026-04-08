using System.Text;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// Tiny shared JSON string-escape helper used by the telemetry writers.
/// We hand-roll this rather than pulling in source-gen JSON because the
/// Telemetry writers only ever produce flat shapes (string arrays, dicts,
/// and 5-field result records) and AOT-clean JSON source-gen for one-off
/// shapes is more machinery than ~30 lines of <see cref="StringBuilder"/>.
/// </summary>
internal static class JsonStringWriter
{
    public static void AppendEscaped(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
