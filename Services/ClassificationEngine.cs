using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MailPulse.Services
{
    public class ClassificationEngine
    {
        /// <summary>
        /// Trigger rule: subject keyword hit OR body regex pattern hit (either one suffices).
        /// </summary>
        public Models.ClassifyResult Evaluate(string subject, string body, string from, string accountName, List<Models.RuleConfig> rules)
        {
            var result = new Models.ClassifyResult { From = from ?? "", AccountName = accountName, Summary = subject ?? "" };
            if (string.IsNullOrEmpty(subject) && string.IsNullOrEmpty(body)) return result;

            foreach (var rule in rules)
            {
                // sender filter
                if (rule.SenderWhitelist != null && rule.SenderWhitelist.Count > 0 &&
                    !rule.SenderWhitelist.Any(w => (from ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                bool subjectHit = rule.SubjectKeywords != null && rule.SubjectKeywords.Count > 0 &&
                                  rule.SubjectKeywords.Any(k => (subject ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                // no body patterns -> only subject keyword can trigger
                if (!subjectHit && (rule.BodyPatterns == null || rule.BodyPatterns.Count == 0)) continue;

                string firstMatch = null;
                foreach (var pat in rule.BodyPatterns ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(pat)) continue;
                    try
                    {
                        var m = Regex.Match(body ?? subject ?? "", pat, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        if (m.Success) { firstMatch = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value; break; }
                    }
                    catch { /* invalid regex in config */ }
                }

                if (!subjectHit && firstMatch == null) continue;   // neither keyword nor pattern hit

                result.Matched = true;
                if (rule.NotifyWithCode) result.Code = firstMatch;
                if (rule.NotifyWithLink)
                {
                    try
                    {
                        var um = Regex.Match(body ?? "", @"https?://[^\s""<>]+", RegexOptions.IgnoreCase);
                        result.Url = um.Success ? um.Value : null;
                    }
                    catch { }
                }
                return result;
            }
            return result;
        }
    }
}
