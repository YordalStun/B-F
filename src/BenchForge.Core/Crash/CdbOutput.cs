using System.Text.RegularExpressions;

namespace BenchForge.Core.Crash;

/// <summary>Pulls the useful lines out of "cdb -z dump -c '!analyze -v'" output.</summary>
public static class CdbOutput
{
    private static readonly string[] Keys = { "BUGCHECK_CODE", "BUGCHECK_STR", "MODULE_NAME", "IMAGE_NAME", "PROCESS_NAME", "FAILURE_BUCKET_ID", "SYMBOL_NAME", "DEFAULT_BUCKET_ID" };

    public static Dictionary<string, string> Parse(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (var k in Keys)
        {
            var m = Regex.Match(output, "^" + k + @":\s*(.+)$", RegexOptions.Multiline);
            if (m.Success) d[k] = m.Groups[1].Value.Trim();
        }
        return d;
    }
}
