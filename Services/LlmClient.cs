using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailPulse.Models;
using Newtonsoft.Json.Linq;

namespace MailPulse.Services
{
    // Shared protocol implementation; each operation owns its transport and cancellation lifetime.
    internal static class LlmClient
    {
        internal static Func<HttpClient> ClientFactory = CreateClient;

        static LlmClient()
        {
            // net48 ServicePointManager caps connections per host at 2 by default, applied to
            // each ServicePoint (keyed by scheme+host+port, shared across handlers, not per
            // client). A slow or cancelled classification alongside a translation can saturate
            // both slots, leaving later requests queued in SendAsync; a gateway that keeps the
            // socket open after a cancelled read can keep a slot busy until it closes the
            // connection. Raise the global cap so a stalled request no longer exhausts the pool.
            ServicePointManager.DefaultConnectionLimit = 8;
        }

        private static HttpClient CreateClient() => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        internal static async Task<string> CompleteAsync(LlmConfig cfg, string system, string message,
            int maxOutputTokens, CancellationToken token)
        {
            var payload = new JObject { ["model"] = cfg.Model, ["stream"] = false };
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

            string requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var elapsed = Stopwatch.StartNew();
            string stage = "sending";
            Logger.Info("LLM transport " + requestId + " started; protocol=" + cfg.Protocol);
            try
            {
                // net48 uses handler-specific connection groups; each call gets its own group so
                // ConnectionClose never affects another request, and the dispose below frees the
                // socket. ExpectContinue is off because some gateways wait for the interim 100
                // response before doing work, which previously stalled translations at "sending".
                using (var http = ClientFactory())
                using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + path))
                {
                    req.Headers.ExpectContinue = false;
                    req.Headers.ConnectionClose = true;
                    string key = SecureStore.Unprotect(cfg.EncryptedApiKey);
                    if (cfg.Protocol == LlmProtocol.Anthropic)
                    {
                        req.Headers.TryAddWithoutValidation("x-api-key", key);
                        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    }
                    else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    req.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                    // Do not buffer until transport EOF: some gateways keep the connection open after the JSON is complete.
                    using (var response = await AwaitWithCancellationAsync(
                        http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token), token,
                        lateResponse => lateResponse.Dispose()).ConfigureAwait(false))
                    {
                        stage = "headers";
                        Logger.Info("LLM transport " + requestId + " headers; status=" + (int)response.StatusCode +
                            "; elapsedMs=" + elapsed.ElapsedMilliseconds);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("LLM 请求失败（HTTP " + (int)response.StatusCode +
                                "），请检查 LLM 配置、模型权限或服务额度。");
                        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("模型网关返回了流式响应，但本次请求明确使用非流式 JSON；请检查网关的响应转换设置。");
                        JObject json;
                        stage = "body";
                        // Stream.ReadAsync cancellation alone is not reliable for every net48 handler.
                        using (token.Register(() => response.Dispose()))
                        using (var stream = await AwaitWithCancellationAsync(response.Content.ReadAsStreamAsync(), token,
                            lateStream => lateStream.Dispose()).ConfigureAwait(false))
                        {
                            json = await ReadJsonResponseAsync(stream, token, count =>
                                Logger.Info("LLM transport " + requestId + " first bytes=" + count +
                                    "; elapsedMs=" + elapsed.ElapsedMilliseconds)).ConfigureAwait(false);
                        }
                        stage = "parsed";
                        Logger.Info("LLM transport " + requestId + " JSON complete; elapsedMs=" + elapsed.ElapsedMilliseconds);
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
            catch (Exception ex)
            {
                Logger.Warn("LLM transport " + requestId + " stopped; stage=" + stage +
                    "; exception=" + ex.GetType().Name + "; cancelled=" + token.IsCancellationRequested +
                    "; elapsedMs=" + elapsed.ElapsedMilliseconds);
                if (token.IsCancellationRequested) throw new OperationCanceledException("LLM request cancelled.", ex, token);
                throw;
            }
        }

        internal static async Task<T> AwaitWithCancellationAsync<T>(Task<T> pending, CancellationToken token,
            Action<T> releaseLateResult = null)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false) != pending || token.IsCancellationRequested)
                {
                    // Observe faults and release late responses even when the handler ignores cancellation.
                    _ = pending.ContinueWith(done =>
                    {
                        if (done.Status == TaskStatus.RanToCompletion) releaseLateResult?.Invoke(done.Result);
                        else if (done.IsFaulted) { var observed = done.Exception; }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new OperationCanceledException(token);
                }
                return await pending.ConfigureAwait(false);
            }
        }

        internal static async Task<JObject> ReadJsonResponseAsync(Stream stream, CancellationToken token,
            Action<int> onFirstBytes)
        {
            const int maxBytes = 4 * 1024 * 1024;
            var chunk = new byte[4096];
            bool started = false, inString = false, escaped = false, first = true;
            int depth = 0;
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = await AwaitWithCancellationAsync(stream.ReadAsync(chunk, 0, chunk.Length, token), token)
                        .ConfigureAwait(false);
                    if (count == 0) throw new InvalidOperationException("模型响应在完整 JSON 返回前结束，请重试。已完成的翻译段落保留。");
                    if (first) { first = false; onFirstBytes?.Invoke(count); }
                    for (int i = 0; i < count; i++)
                    {
                        byte value = chunk[i];
                        body.WriteByte(value);
                        if (body.Length > maxBytes) throw new InvalidOperationException("模型响应过大，已停止读取，请更换模型或缩短邮件。");
                        if (!started)
                        {
                            // ASCII framing bytes cannot occur inside UTF-8 multibyte characters.
                            if (value == ' ' || value == '\r' || value == '\n' || value == '\t' ||
                                (body.Length <= 3 && (value == 0xef || value == 0xbb || value == 0xbf))) continue;
                            if (value != '{') throw new InvalidOperationException("模型网关未返回 JSON 对象，请检查响应格式和网关设置。");
                            started = true; depth = 1;
                            continue;
                        }
                        if (inString)
                        {
                            if (escaped) escaped = false;
                            else if (value == '\\') escaped = true;
                            else if (value == '"') inString = false;
                            continue;
                        }
                        if (value == '"') inString = true;
                        else if (value == '{' || value == '[') depth++;
                        else if (value == '}' || value == ']')
                        {
                            if (--depth == 0)
                            {
                                token.ThrowIfCancellationRequested();
                                return JObject.Parse(new UTF8Encoding(false, true).GetString(body.ToArray()).TrimStart('\uFEFF'));
                            }
                        }
                    }
                }
            }
        }
    }
}
