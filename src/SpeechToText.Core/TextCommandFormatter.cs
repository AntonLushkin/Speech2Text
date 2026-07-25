using System;
using System.Text.RegularExpressions;

namespace SpeechToText.Core
{
    public static class TextCommandFormatter
    {
        private static readonly Regex NewParagraph =
            Command("новый абзац", consumeLeadingSpace: true);
        private static readonly Regex NewLine =
            Command("новая строка", consumeLeadingSpace: true);
        private static readonly Regex OpenQuotes =
            Command("открой кавычки", consumeLeadingSpace: false);
        private static readonly Regex CloseQuotes =
            Command("закрой кавычки", consumeLeadingSpace: true);

        public static string Apply(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var result = NewParagraph.Replace(text, "\n\n");
            result = NewLine.Replace(result, "\n");
            result = OpenQuotes.Replace(result, "«");
            result = CloseQuotes.Replace(result, "»");
            result = Regex.Replace(result, @"[ \t]+\n", "\n");
            result = Regex.Replace(result, @"\n[ \t]+", "\n");
            result = Regex.Replace(result, @"\n{3,}", "\n\n");
            return result.Trim();
        }

        private static Regex Command(
            string phrase,
            bool consumeLeadingSpace)
        {
            var leading = consumeLeadingSpace ? @"\s*" : string.Empty;
            return new Regex(
                leading + @"\b" + Regex.Escape(phrase) +
                @"\b[\s,.:;!?…]*",
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant |
                RegexOptions.IgnoreCase);
        }
    }
}
