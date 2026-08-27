using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MailPulse.Services
{
    /// <summary>Repairs UTF-8 text that a sender incorrectly labelled and decoded as GBK/GB2312.</summary>
    public static class TextEncodingRepair
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding Gbk = Encoding.GetEncoding(936,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private static readonly Regex NonAsciiRuns = new Regex("[^\\x00-\\x7F]+", RegexOptions.Compiled);
        private static readonly string SuspiciousCharacters =
            "浣犵殑楠岃瘉鐮佹槸鐐瑰嚮浠ヤ笅閾炬帴纭璁鍙戦鍐呭鏍囬";
        private static readonly string[] SuspiciousSequences =
        {
            "浣犵殑", "楠岃瘉", "鐐瑰嚮", "閾炬帴", "纭", "鏄惁", "鍙戦", "鍐呭"
        };

        public static string Repair(string value)
        {
            if (string.IsNullOrEmpty(value) || SuspicionScore(value) < 2) return value;
            return NonAsciiRuns.Replace(value, match => RepairSegment(match.Value));
        }

        private static string RepairSegment(string value)
        {
            int before = SuspicionScore(value);
            if (before == 0) return value;
            try
            {
                string candidate = StrictUtf8.GetString(Gbk.GetBytes(value));
                return SuspicionScore(candidate) < before ? candidate : value;
            }
            catch { return value; }
        }

        private static int SuspicionScore(string value)
        {
            int score = 0;
            foreach (char c in value)
            {
                if (c == '\uFFFD' || (c >= '\uE000' && c <= '\uF8FF')) score += 2;
                else if (SuspiciousCharacters.IndexOf(c) >= 0) score++;
            }
            foreach (string sequence in SuspiciousSequences)
                if (value.IndexOf(sequence, StringComparison.Ordinal) >= 0) score += 3;
            return score;
        }
    }
}
