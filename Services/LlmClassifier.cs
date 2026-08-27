using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MailPulse.Models;

namespace MailPulse.Services
{
    /// <summary>
    /// LLM fallback classifier supporting OpenAI Chat Completions, OpenAI Responses,
    /// and Anthropic Messages protocols.
    /// </summary>
    public class LlmClassifier
    {
        public static LlmConfig FirstEnabled(List<Models.LlmConfig> list)
        {
            if (list == null) return null;
            foreach (var c in list)
                if (c.Enabled && !string.IsNullOrWhiteSpace(c.Model) &&
                    !string.IsNullOrWhiteSpace(SecureStore.Unprotect(c.EncryptedApiKey)))
                    return c;
            return null;
        }

        public static string BuildMessage(string prompt, string subject, string body)
        {
            string msg = string.IsNullOrWhiteSpace(prompt) ? Models.AppConfig.DefaultLlmPrompt : prompt;
            string s = subject ?? "";
            string b = body ?? "";
            if (msg.Contains("{subject}") || msg.Contains("{body}"))
                msg = msg.Replace("{subject}", s).Replace("{body}", b);
            else
                msg += Environment.NewLine + Environment.NewLine + "邮件主题：" + s + Environment.NewLine + "邮件正文：" + b;
            return msg;
        }

        public async Task<Models.ClassifyResult> ClassifyAsync(
            string subject, string body, string from, string accountName,
            Models.LlmConfig cfg, string prompt, CancellationToken token)
        {
            var result = new Models.ClassifyResult
            {
                From = from ?? "",
                AccountName = accountName,
                Summary = subject ?? "",
                IsAiAgent = true
            };
            string raw = null;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, cfg.TimeoutSeconds)));
                try
                {
                    raw = await LlmClient.CompleteAsync(cfg,
                        "You are a precise mail classifier that only outputs JSON.",
                        BuildMessage(prompt, subject, body), 512, cts.Token);
                    ApplyJson(raw, result);
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("LLM classify timeout for " + cfg.Name);
                }
                catch (Exception ex)
                {
                    Logger.Error("LLM classify failed for " + cfg.Name, ex);
                }
            }
            return result;
        }

        private static void ApplyJson(string raw, Models.ClassifyResult result)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            int s = raw.IndexOf('{');
            int e = raw.LastIndexOf('}');
            if (s < 0 || e <= s) return;
            try
            {
                var obj = JObject.Parse(raw.Substring(s, e - s + 1));
                bool urgent = obj.Value<bool?>("is_urgent") ?? false;
                if (!urgent) return;
                result.Matched = true;
                result.Code = obj.Value<string>("code");
                result.Url = obj.Value<string>("url");
                if (string.IsNullOrEmpty(result.Code)) result.Code = null;
                if (string.IsNullOrEmpty(result.Url)) result.Url = null;
            }
            catch { }
        }

    }
}

