using System.Text;

namespace FileZipPreview.Services;

internal static class NaturalSortKey
{
    public static string Build(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var current = value[index];
            if (!char.IsDigit(current))
            {
                builder.Append(char.ToLowerInvariant(current));
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length && char.IsDigit(value[index]))
            {
                index++;
            }

            var digits = value[start..index];
            var significant = digits.TrimStart('0');
            if (significant.Length == 0)
            {
                significant = "0";
            }

            builder
                .Append('#')
                .Append(significant.Length.ToString("D10"))
                .Append(':')
                .Append(significant)
                .Append(':')
                .Append(digits.Length.ToString("D10"))
                .Append(';');
        }

        return builder.ToString();
    }
}
