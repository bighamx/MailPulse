using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailPulse.Models;
using Newtonsoft.Json.Linq;

namespace MailPulse.Services
{
    // Shared transport: credentials, model and endpoint come from the existing LLM settings.
    internal static class LlmClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        internal static async Task<string> CompleteAsync(LlmConfig cfg, string system, string message,
            int maxOutputTokens, CancellationToken token)
        {
            var payload = new JObject { ["model"] = cfg.Model };
            string path;
            switch (cfg.Protocol)
            {
                case LlmProtocol.OpenAiChat:
                    path = "/chat/completions";
                    payload["temperature"] = 0;
                    // Keep classification's existing Chat payload compatible with custom endpoints.
                    if (maxOutputTokens != 512) payload["max_tokens"] = maxOutputTokens;
                    payload["messages"] = new JArray(
                        new JObject { ["role"] = "system", ["content"] = system },
                        new JObject { ["role"] = "user", ["content"] = message });
                    break;
                case LlmProtocol.OpenAiResponses:
                    path = "/responses";
                    payload["temperature"] = 0;
                    payload["max_output_tokens"] = maxOutputTokens;
                    payload["instructions"] = system;
                    payload["input"] = message;
                    break;
                case LlmProtocol.Anthropic:
                    path = "/messages";
                    payload["max_tokens"] = maxOutputTokens;
                    payload["system"] = system;
                    payload["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = message });
                    break;
                default: throw new InvalidOperationException("不支持的 LLM 协议。");
            }

            using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + path))
            {
                string key = SecureStore.Unprotect(cfg.EncryptedApiKey);
                if (cfg.Protocol == LlmProtocol.Anthropic)
                {
                    req.Headers.TryAddWithoutValidation("x-api-key", key);
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(req, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("LLM 请求失败（HTTP " + (int)response.StatusCode +
                            "），请检查 LLM 配置、模型权限或服务额度。");
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    token.ThrowIfCancellationRequested();
                    if (json["choices"]?[0]?["finish_reason"]?.Value<string>() == "length" ||
                        json.Value<string>("stop_reason") == "max_tokens" || json.Value<string>("status") == "incomplete")
                        throw new InvalidOperationException("LLM 输出未完成，邮件可能过长。请换用支持更长输出的模型后重试。");
                    if (cfg.Protocol == LlmProtocol.OpenAiChat)
                        return json["choices"]?[0]?["message"]?["content"]?.Value<string>();
                    var text = new StringBuilder();
                    if (cfg.Protocol == LlmProtocol.Anthropic)
                    {
                        foreach (var block in json["content"] as JArray ?? new JArray())
                            if (block.Value<string>("type") == "text") text.Append(block.Value<string>("text"));
                    }
                    else
                    {
                        foreach (var item in json["output"] as JArray ?? new JArray())
                        {
                            if (item.Value<string>("type") != "message") continue;
                            foreach (var block in item["content"] as JArray ?? new JArray())
                                if (block.Value<string>("type") == "output_text") text.Append(block.Value<string>("text"));
                        }
                    }
                    return text.ToString();
                }
            }
        }
    }
}
