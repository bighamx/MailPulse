using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
        private static readonly HttpClient Http = new HttpClient();

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
            var result = new Models.ClassifyResult { From = from ?? "", AccountName = accountName, Summary = subject ?? "" };
            string raw = null;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, cfg.TimeoutSeconds)));
                try
                {
                    switch (cfg.Protocol)
                    {
                        case Models.LlmProtocol.OpenAiChat:
                            raw = await CallOpenAiChatAsync(cfg, prompt, subject, body, cts.Token);
                            break;
                        case Models.LlmProtocol.OpenAiResponses:
                            raw = await CallOpenAiResponsesAsync(cfg, prompt, subject, body, cts.Token);
                            break;
                        case Models.LlmProtocol.Anthropic:
                            raw = await CallAnthropicAsync(cfg, prompt, subject, body, cts.Token);
                            break;
                    }
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

        private static async Task<string> CallOpenAiChatAsync(
            Models.LlmConfig cfg, string prompt, string subject, string body, CancellationToken token)
        {
            string msg = BuildMessage(prompt, subject, body);
            var payload = new JObject
            {
                ["model"] = cfg.Model,
                ["temperature"] = 0,
                ["messages"] = new JArray(
                    new JObject { ["role"] = "system", ["content"] = "You are a precise mail classifier that only outputs JSON." },
                    new JObject { ["role"] = "user", ["content"] = msg })
            };
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SecureStore.Unprotect(cfg.EncryptedApiKey));
            req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await Http.SendAsync(req, token);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["choices"]?[0]?["message"]?["content"]?.Value<string>();
        }

        private static async Task<string> CallOpenAiResponsesAsync(
            Models.LlmConfig cfg, string prompt, string subject, string body, CancellationToken token)
        {
            string msg = BuildMessage(prompt, subject, body);
            var payload = new JObject
            {
                ["model"] = cfg.Model,
                ["temperature"] = 0,
                ["max_output_tokens"] = 512,
                ["input"] = msg
            };
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/responses");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SecureStore.Unprotect(cfg.EncryptedApiKey));
            req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await Http.SendAsync(req, token);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var sb = new StringBuilder();
            foreach (var item in json["output"] as JArray ?? new JArray())
            {
                if (item.Value<string>("type") != "message") continue;
                foreach (var c in item["content"] as JArray ?? new JArray())
                {
                    string txt = c.Value<string>("text");
                    if (txt != null) sb.AppendLine(txt);
                }
            }
            return sb.ToString();
        }

        private static async Task<string> CallAnthropicAsync(
            Models.LlmConfig cfg, string prompt, string subject, string body, CancellationToken token)
        {
            string msg = BuildMessage(prompt, subject, body);
            var payload = new JObject
            {
                ["model"] = cfg.Model,
                ["max_tokens"] = 512,
                ["system"] = "You are a precise mail classifier that only outputs JSON.",
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = msg })
            };
            var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/messages");
            req.Headers.TryAddWithoutValidation("x-api-key", SecureStore.Unprotect(cfg.EncryptedApiKey));
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp = await Http.SendAsync(req, token);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["content"]?[0]?["text"]?.Value<string>();
        }
    }
}

