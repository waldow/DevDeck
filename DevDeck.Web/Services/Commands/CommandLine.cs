using System.Text;

namespace DevDeck.Web.Services.Commands;

/// <summary>
/// Quoting for <c>ProcessStartInfo.Arguments</c>, which .NET splits into argv the way the
/// Windows C runtime does on every OS: whitespace separates arguments outside double quotes,
/// and backslashes are literal unless they precede a quote.
/// </summary>
public static class CommandLine
{
    /// <summary><paramref name="value"/> as one argument: quoted when it is empty or holds whitespace or quotes.</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        return "\"" + EscapeInsideQuotes(value, followedByQuote: true) + "\"";
    }

    /// <summary>
    /// Escapes a value placed between double quotes: a quote becomes \" (with any backslashes
    /// before it doubled), and trailing backslashes are doubled when a closing quote follows.
    /// </summary>
    public static string EscapeInsideQuotes(string value, bool followedByQuote)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                sb.Append('\\', followedByQuote ? backslashes * 2 : backslashes);
                break;
            }

            if (value[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether position <paramref name="index"/> of an argument string falls inside a quoted
    /// region (an odd run of backslashes escapes a quote; "" inside quotes is a literal quote).
    /// </summary>
    public static bool IsInsideQuotes(string arguments, int index)
    {
        var inQuotes = false;
        var i = 0;
        while (i < index)
        {
            var backslashes = 0;
            while (i < index && arguments[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i >= index) break;
            if (arguments[i] == '"' && backslashes % 2 == 0)
            {
                if (inQuotes && i + 1 < index && arguments[i + 1] == '"')
                {
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            i++;
        }
        return inQuotes;
    }
}
