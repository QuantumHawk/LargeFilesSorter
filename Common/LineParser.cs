using System.Globalization;
using System.Text;

namespace Common
{
    public static class LineParser
    {
        /// <summary>
        /// Parses "Number. Text" and stores Text as UTF-8 bytes to avoid UTF-16 string allocation
        /// throughout the sort pipeline. Decoding back to string happens only at final output.
        /// </summary>
        public static bool TryParse(string line, out Record record)
        {
            record = default;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            ReadOnlySpan<char> span = line.AsSpan();
            int dotIndex = span.IndexOf('.');
            if (dotIndex <= 0)
                return false;

            ReadOnlySpan<char> numberSpan = span[..dotIndex].Trim();
            if (!ulong.TryParse(numberSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
                return false;

            ReadOnlySpan<char> textSpan = span[(dotIndex + 1)..].TrimStart();
            byte[] utf8 = Encoding.UTF8.GetBytes(textSpan.ToString());
            record = new Record(number, utf8);
            return true;
        }
    }
}