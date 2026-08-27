param([string]$PreviewPath, [string]$ThemeMode = 'Light',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
# Run with Windows PowerShell (-STA). No real mail, API key or network is used.
$repoPath = Split-Path $PSScriptRoot -Parent
$exePath = Join-Path $repoPath ("bin\{0}\net48\MailPulse.exe" -f $Configuration)
$jsonPath = Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
$wpfPath = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'WPF'
Add-Type -Path $jsonPath
Add-Type -Path (Join-Path $wpfPath 'PresentationFramework.dll')
Add-Type -Path (Join-Path $wpfPath 'PresentationCore.dll')
Add-Type -AssemblyName WindowsBase, System.Net.Http
[void][Reflection.Assembly]::LoadFrom($exePath)
$references = @($exePath, $jsonPath, 'System.dll', 'System.Core.dll', 'System.Net.Http.dll',
    'System.Security.dll', (Join-Path $wpfPath 'WindowsBase.dll'), (Join-Path $wpfPath 'PresentationFramework.dll'),
    (Join-Path $wpfPath 'PresentationCore.dll'), 'System.Xaml.dll')
$compiler = New-Object System.CodeDom.Compiler.CompilerParameters
$compiler.ReferencedAssemblies.AddRange([string[]]$references)
Add-Type -CompilerParameters $compiler -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailPulse.Models;
using MailPulse.Services;
using MailPulse.UI;
using Newtonsoft.Json.Linq;

public static class MailTranslationSmokeTests
{
    sealed class Stub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Handle(request, token);
        }
    }
    // A gateway has sent the complete JSON but does not send EOF / the final HTTP chunk.
    sealed class NonClosingStream : Stream
    {
        readonly byte[] Bytes;
        readonly int ChunkSize;
        int PositionInBytes;
        public bool ReadPastPayload;
        public bool Disposed;
        public NonClosingStream(string text, int chunkSize)
        { Bytes = Encoding.UTF8.GetBytes(text); ChunkSize = chunkSize; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { return PositionInBytes; } set { throw new NotSupportedException(); } }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (PositionInBytes == Bytes.Length)
            {
                ReadPastPayload = true;
                return new TaskCompletionSource<int>().Task; // Intentionally ignores cancellation.
            }
            int size = Math.Min(Math.Min(count, ChunkSize), Bytes.Length - PositionInBytes);
            Array.Copy(Bytes, PositionInBytes, buffer, offset, size);
            PositionInBytes += size;
            return Task.FromResult(size);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
    static readonly List<string> Results = new List<string>();
    // Parallel completion reports arrive out of order; keep every snapshot thread-safely.
    sealed class CaptureProgress : IProgress<MailTranslationProgress>
    {
        readonly object Gate = new object();
        public readonly List<MailTranslationProgress> All = new List<MailTranslationProgress>();
        public MailTranslationProgress Last;
        public int MaxCompleted { get { lock (Gate) return All.Count == 0 ? 0 : All.Max(p => p.CompletedParts); } }
        public MailTranslationProgress Find(int completed)
        {
            lock (Gate) return All.FirstOrDefault(p => p.CompletedParts == completed && p.Snapshot != null);
        }
        public void Report(MailTranslationProgress value) { lock (Gate) { All.Add(value); Last = value; } }
    }
    sealed class CaptureHtmlProgress : IProgress<HtmlTranslationProgress>
    {
        readonly object Gate = new object();
        public readonly List<HtmlTranslationProgress> All = new List<HtmlTranslationProgress>();
        public HtmlTranslationProgress FindWhere(Func<HtmlTranslationProgress, bool> predicate)
        {
            lock (Gate) return All.FirstOrDefault(predicate);
        }
        public void Report(HtmlTranslationProgress value) { lock (Gate) All.Add(value); }
    }
    static string ReadString(HttpContent content)
    {
        return content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
    static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Results.Add("PASS: " + label);
    }
    static HttpResponseMessage Reply(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
    static string Envelope(LlmProtocol protocol, string content)
    {
        if (protocol == LlmProtocol.OpenAiChat)
            return Obj("choices", new JArray(Obj("finish_reason", "stop", "message", Obj("content", content)))).ToString();
        if (protocol == LlmProtocol.Anthropic)
            return Obj("stop_reason", "end_turn", "content", new JArray(Obj("type", "text", "text", content))).ToString();
        return Obj("status", "completed", "output", new JArray(Obj("type", "message", "content",
            new JArray(Obj("type", "output_text", "text", content))))).ToString();
    }
    static JObject Obj(params object[] pairs)
    {
        var obj = new JObject();
        for (int i = 0; i < pairs.Length; i += 2) obj[(string)pairs[i]] = JToken.FromObject(pairs[i + 1]);
        return obj;
    }
    static async Task ServeUnterminatedResponse(TcpListener listener, string json, Task stop)
    {
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(single, 0, 1) == 0) throw new IOException("Missing test request headers");
                header.Append((char)single[0]);
                if (header.Length > 16000) throw new IOException("Test request headers too large");
            }
            if (header.ToString().IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("LLM request must not wait for an interim 100 Continue response");
            if (header.ToString().IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("LLM request must close the gateway connection after completion");
            int remaining = int.Parse(Regex.Match(header.ToString(), "Content-Length: ([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value);
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining));
                if (count == 0) throw new IOException("Missing test request body");
                remaining -= count;
            }
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n" + payload.Length.ToString("X") + "\r\n");
            await stream.WriteAsync(response, 0, response.Length);
            await stream.WriteAsync(payload, 0, payload.Length);
            await stream.WriteAsync(new byte[] { 13, 10 }, 0, 2);
            await stream.FlushAsync();
            // No terminating zero chunk. Server stays connected until the test is complete.
            await stop;
        }
    }
    static string TranslationJson(string subject)
    {
        return Obj("subject", subject, "body", "你好\n验证码 DE64FWEF\nhttps://example.com/?token=abc").ToString();
    }
    static void Throws<T>(Func<Task> action, string label) where T : Exception
    {
        bool caught = false;
        try { action().GetAwaiter().GetResult(); } catch (T) { caught = true; }
        Check(caught, label);
    }
    static FieldInfo Field(string name) { return typeof(MailCenterWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    static T Get<T>(object window, string name) { return (T)window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window); }
    static void Set(MailCenterWindow window, string name, object value) { Field(name).SetValue(window, value); }
    static object Invoke(object window, string name)
    {
        return window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
    }
    static object Invoke(object window, string name, params object[] args)
    {
        var method = window.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == (args == null ? 0 : args.Length));
        return method.Invoke(window, args ?? new object[0]);
    }
    static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > until) throw new Exception("UI task timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    static MailMessageContent SetMessage(MailCenterWindow window, string subject)
    {
        var message = new MailMessageContent { Subject = subject, Body = "Hello\nVerification code DE64FWEF", From = "sender@example.com" };
        Set(window, "_currentMessage", message);
        Set(window, "_messageCts", new CancellationTokenSource());
        Invoke(window, "ShowOriginalMessage");
        return message;
    }
    public static string[] Run(string previewPath, string themeMode)
    {
        string originalLogDir = Logger.LogDir;
        Logger.LogDir = Path.Combine(Path.GetDirectoryName(typeof(LlmClassifier).Assembly.Location), "test-logs");
        var stub = new Stub();
        var clientType = typeof(LlmClassifier).Assembly.GetType("MailPulse.Services.LlmClient");
        var httpField = clientType.GetField("ClientFactory", BindingFlags.Static | BindingFlags.NonPublic);
        var originalHttp = httpField.GetValue(null);
        Func<HttpClient> fakeHttp = () => new HttpClient(stub, false) { Timeout = Timeout.InfiniteTimeSpan };
        httpField.SetValue(null, fakeHttp);
        var cfg = new LlmConfig { BaseUrl = "https://stub.invalid/v1", Model = "test-model", EncryptedApiKey = SecureStore.Protect("fake-key") };
        var translator = new MailTranslationService();
        try
        {
            var edit = new LlmConfigDialog(cfg);
            try
            {
                Check(Get<TextBox>(edit, "_tbKey").Text == "", "editing never reveals saved API key");
                Get<TextBox>(edit, "_tbModel").Text = "changed-model";
                Get<TextBox>(edit, "_tbTimeout").Text = "30";
                Get<TextBox>(edit, "_tbName").Text = "changed-name";
                Check(Invoke(edit, "ValidateInputs") == null, "other settings can be saved without re-entering key");
                var changed = edit.Result();
                Check(changed.EncryptedApiKey == cfg.EncryptedApiKey && changed.Id == cfg.Id &&
                    changed.Model == "changed-model" && changed.TimeoutSeconds == 30, "editing preserves exact saved ciphertext and identity");
                Get<TextBox>(edit, "_tbKey").Text = "  ";
                Check(Invoke(edit, "ValidateInputs") == null && edit.Result().EncryptedApiKey == cfg.EncryptedApiKey,
                    "whitespace key also preserves saved key");
                Get<TextBox>(edit, "_tbKey").Text = " replacement-key ";
                Check(SecureStore.Unprotect(edit.Result().EncryptedApiKey) == "replacement-key", "explicit replacement key is trimmed and encrypted");
            }
            finally { edit.Close(); }
            foreach (var initial in new LlmConfig[] { null, new LlmConfig { EncryptedApiKey = "invalid-ciphertext" } })
            {
                var missingKey = new LlmConfigDialog(initial);
                try
                {
                    Get<TextBox>(missingKey, "_tbName").Text = "test";
                    Check(Invoke(missingKey, "ValidateInputs") != null, "new or unreadable saved key requires input");
                    Get<TextBox>(missingKey, "_tbKey").Text = "new-key";
                    Check(Invoke(missingKey, "ValidateInputs") == null, "providing new key allows save");
                }
                finally { missingKey.Close(); }
            }
            foreach (LlmProtocol protocol in Enum.GetValues(typeof(LlmProtocol)))
            {
                cfg.Protocol = protocol;
                stub.Handle = async (request, token) => {
                    Check(request.Headers.ExpectContinue == false && request.Headers.ConnectionClose == true,
                        protocol + " avoids interim handshake and connection reuse");
                    var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    string path = protocol == LlmProtocol.OpenAiChat ? "/chat/completions" : protocol == LlmProtocol.Anthropic ? "/messages" : "/responses";
                    Check(request.RequestUri.AbsolutePath == "/v1" + path, protocol + " endpoint");
                    string system = (string)(payload["system"] ?? payload["instructions"] ?? payload["messages"][0]["content"]);
                    Check(system.Contains("不可信数据"), protocol + " translation prompt isolated from mail instructions");
                    string input = protocol == LlmProtocol.OpenAiResponses ? (string)payload["input"] :
                        (string)payload["messages"][protocol == LlmProtocol.OpenAiChat ? 1 : 0]["content"];
                    var mail = JObject.Parse(input);
                    Check((string)mail["subject"] == "Hello" && ((string)mail["body"]).Contains("DE64FWEF"), protocol + " mail input");
                    Check((int)(payload["max_tokens"] ?? payload["max_output_tokens"]) == 8192, protocol + " translation output budget");
                    return Reply(Envelope(protocol, TranslationJson("你好")));
                };
                var result = translator.TranslateAsync("Hello", "Verification code DE64FWEF", cfg, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Subject == "你好" && result.Body.Contains("DE64FWEF") && result.Body.Contains("https://example.com/?token=abc"), protocol + " translated result");
                stub.Handle = async (request, token) => {
                    var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    Check(protocol == LlmProtocol.OpenAiChat ? payload["max_tokens"] == null :
                        (int)(payload["max_tokens"] ?? payload["max_output_tokens"]) == 512, protocol + " classifier budget unchanged");
                    return Reply(Envelope(protocol, "{\"is_urgent\":true,\"code\":\"DE64FWEF\",\"url\":null}"));
                };
                var classified = new LlmClassifier().ClassifyAsync("Hello", "DE64FWEF", "sender", "test", cfg, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(classified.Matched && classified.Code == "DE64FWEF" && classified.IsAiAgent, protocol + " classifier regression");
            }
            cfg.Protocol = LlmProtocol.OpenAiChat;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var stopServer = new TaskCompletionSource<bool>();
            var server = Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
                ServeUnterminatedResponse(listener, Envelope(cfg.Protocol, TranslationJson("你好")), stopServer.Task)));
            string oldBaseUrl = cfg.BaseUrl;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    httpField.SetValue(null, originalHttp);
                    cfg.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1";
                    for (int round = 0; round < 3; round++)
                    {
                        var actualNetwork = translator.TranslateAsync("Hello", "Body", cfg, deadline.Token);
                        Check(Task.WhenAny(actualNetwork, Task.Delay(2500)).GetAwaiter().GetResult() == actualNetwork,
                            "real net48 repeat " + round + " finishes without terminating chunk or server disconnect");
                        Check(actualNetwork.GetAwaiter().GetResult().Subject == "你好", "real chunked HTTP response decoded");
                    }
                }
                finally
                {
                    deadline.Cancel(); stopServer.TrySetResult(true); listener.Stop();
                    httpField.SetValue(null, fakeHttp); cfg.BaseUrl = oldBaseUrl;
                    if (!server.Wait(2000)) throw new Exception("Loopback test server did not stop");
                }
            }
            foreach (LlmProtocol protocol in Enum.GetValues(typeof(LlmProtocol)))
            {
                cfg.Protocol = protocol;
                var gatewayStream = new NonClosingStream("\uFEFF \r\n" + Envelope(protocol,
                    Obj("subject", "你好", "body", "引号\"、反斜线\\、括号{}[]、emoji😀和 CODE1234").ToString()), 3);
                stub.Handle = async (req, ct) => {
                    var payload = JObject.Parse(await req.Content.ReadAsStringAsync());
                    Check((bool)payload["stream"] == false, protocol + " explicitly requests non-streaming JSON");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gatewayStream) };
                };
                var rootTask = translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None);
                Check(Task.WhenAny(rootTask, Task.Delay(2000)).GetAwaiter().GetResult() == rootTask,
                    protocol + " complete JSON finishes without HTTP EOF");
                var rootResult = rootTask.GetAwaiter().GetResult();
                Check(rootResult.Subject == "你好" && rootResult.Body.Contains("😀") && rootResult.Body.Contains("{}[]"),
                    protocol + " split UTF8 and escaped JSON strings parsed correctly");
                Check(!gatewayStream.ReadPastPayload && gatewayStream.Disposed, protocol + " does not await trailing gateway bytes and releases stream");
            }
            cfg.Protocol = LlmProtocol.OpenAiChat;
            var aligned = JObject.Parse(Envelope(cfg.Protocol, TranslationJson("你好")));
            aligned["padding"] = "";
            aligned["padding"] = new string('x', 4096 - Encoding.UTF8.GetByteCount(aligned.ToString()));
            var alignedStream = new NonClosingStream(aligned.ToString(), 4096);
            stub.Handle = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(alignedStream) });
            var alignedTask = translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None);
            Check(Task.WhenAny(alignedTask, Task.Delay(2000)).GetAwaiter().GetResult() == alignedTask,
                "JSON ending exactly on read buffer boundary does not wait for EOF");
            alignedTask.GetAwaiter().GetResult();
            var stalledStream = new NonClosingStream("{\"choices\":[", 4096);
            stub.Handle = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalledStream) });
            using (var cancel = new CancellationTokenSource())
            {
                var stalled = translator.TranslateAsync("Hello", "Body", cfg, cancel.Token);
                cancel.Cancel();
                Check(Task.WhenAny(stalled, Task.Delay(2000)).GetAwaiter().GetResult() == stalled,
                    "cancel interrupts a response reader that ignores cancellation");
                Throws<OperationCanceledException>(() => stalled, "stalled body cancellation surfaced");
                Check(stalledStream.Disposed, "stalled response stream disposed on cancellation");
            }
            var lateHeaders = new TaskCompletionSource<HttpResponseMessage>();
            stub.Handle = (req, ct) => lateHeaders.Task;
            using (var cancel = new CancellationTokenSource())
            {
                var stalled = translator.TranslateAsync("Hello", "Body", cfg, cancel.Token);
                cancel.Cancel();
                Check(Task.WhenAny(stalled, Task.Delay(2000)).GetAwaiter().GetResult() == stalled,
                    "cancel interrupts a handler that ignores cancellation before headers");
                Throws<OperationCanceledException>(() => stalled, "stalled headers cancellation surfaced");
                var lateStream = new NonClosingStream("{}", 8);
                lateHeaders.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(lateStream) });
                Check(SpinWait.SpinUntil(() => lateStream.Disposed, 2000), "late HTTP response is disposed after cancellation");
            }
            foreach (string invalid in new[] { "", "not-json", "{}", "{\"subject\":\"ok\",\"body\":\"\"}", "{\"subject\":[],\"body\":\"ok\"}" })
            {
                stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol, invalid)));
                Throws<InvalidOperationException>(() => translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None), "reject invalid/empty translation");
            }
            stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol, "```json\n" + TranslationJson("你好") + "\n```")));
            Check(translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None).GetAwaiter().GetResult().Subject == "你好", "accept JSON code fence");
            int before = stub.Calls;
            Throws<InvalidOperationException>(() => translator.TranslateAsync("Hello", new string('x', 24001), cfg, CancellationToken.None), "reject overlong input without truncation");
            Throws<InvalidOperationException>(() => translator.TranslateAsync("", "", cfg, CancellationToken.None), "reject empty mail");
            Throws<InvalidOperationException>(() => translator.TranslateAsync("Hello", "Body", null, CancellationToken.None), "missing LLM config");
            Check(stub.Calls == before, "invalid input makes no API request");
            stub.Handle = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            Throws<InvalidOperationException>(() => translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None), "HTTP failure surfaced");
            foreach (LlmProtocol protocol in Enum.GetValues(typeof(LlmProtocol)))
            {
                cfg.Protocol = protocol;
                stub.Handle = (req, ct) => Task.FromResult(Reply("{\"choices\":[{\"finish_reason\":\"length\"}],\"stop_reason\":\"max_tokens\",\"status\":\"incomplete\"}"));
                Throws<InvalidOperationException>(() => translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None), protocol + " incomplete output rejected");
            }
            cfg.Protocol = LlmProtocol.OpenAiChat;
            stub.Handle = async (req, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); };
            using (var cancel = new CancellationTokenSource())
            {
                var pending = translator.TranslateAsync("Hello", "Body", cfg, cancel.Token);
                cancel.Cancel();
                Throws<OperationCanceledException>(() => pending, "cancel in-flight request");
            }
            stub.Handle = (req, ct) => { throw new TaskCanceledException(); };
            Throws<HttpRequestException>(() => translator.TranslateAsync("Hello", "Body", cfg, CancellationToken.None), "upstream cancellation is not misreported as local timeout");

            string longBody = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => "SECTION_" + i.ToString("D2") + " " +
                string.Join(" ", Enumerable.Repeat("Keep all paragraphs and reference numbers intact.", 18))));
            var splitMethod = typeof(MailTranslationService).GetMethod("SplitBody", BindingFlags.NonPublic | BindingFlags.Static);
            string splitSample = new string('中', 1785) + "https://example.com/confirm?code=DE64FWEF" +
                "\r\n" + new string('中', 1782) + "😀😀😀😀\r\n" + longBody;
            var splitParts = (List<string>)splitMethod.Invoke(null, new object[] { splitSample });
            Check(string.Concat(splitParts) == splitSample, "splitting preserves every source character");
            Check(splitParts.Any(p => p.Contains("https://example.com/confirm?code=DE64FWEF")), "splitting never breaks a URL");
            Check(splitParts.All(p => !char.IsHighSurrogate(p[p.Length - 1]) && !char.IsLowSurrogate(p[0])), "splitting preserves Unicode surrogate pairs");
            foreach (LlmProtocol protocol in Enum.GetValues(typeof(LlmProtocol)))
            {
                cfg.Protocol = protocol;
                var chunkRequests = new List<JObject>();
                object chunkLock = new object();
                stub.Handle = async (request, ct) => {
                    var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    var input = JObject.Parse(protocol == LlmProtocol.OpenAiResponses ? (string)payload["input"] :
                        (string)payload["messages"][protocol == LlmProtocol.OpenAiChat ? 1 : 0]["content"]);
                    lock (chunkLock) chunkRequests.Add(input);
                    await Task.Delay(60);
                    return Reply(Envelope(protocol, input.ToString()));
                };
                var session = translator.CreateSession("Subject", longBody, cfg);
                var progress = new CaptureProgress();
                var translated = translator.TranslateAsync(session, CancellationToken.None, progress).GetAwaiter().GetResult();
                Check(session.TotalParts > 1 && chunkRequests.Count == session.TotalParts, protocol + " long mail uses independent requests");
                lock (chunkLock)
                {
                    Check(chunkRequests.All(p => ((string)p["body"]).Length <= 1800), protocol + " normal chunks bounded to 1800 characters");
                    Check(chunkRequests.Skip(1).All(p => (string)p["subject"] == ""), protocol + " subject translated only once");
                }
                Check(Enumerable.Range(1, 12).All(i => translated.Body.Contains("SECTION_" + i.ToString("D2"))), protocol + " all sections retained in order");
                Check(progress.MaxCompleted == session.TotalParts && progress.Last.PartTimeoutSeconds == 120, protocol + " per-part timeout independent of 8-second classifier setting");
                Check(progress.All.Any(p => p.Snapshot != null && p.CompletedParts > 1 && p.CompletedParts < session.TotalParts),
                    protocol + " intermediate snapshots expose partial merges");
            }
            cfg.Protocol = LlmProtocol.OpenAiChat;
            // Deterministic parallel proof: reverse-completion delays plus per-section markers.
            int active = 0, peak = 0;
            stub.Handle = async (request, ct) => {
                var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                string section = Regex.Match((string)input["body"], "SECTION_(\\d+)").Groups[1].Value;
                int now = Interlocked.Increment(ref active);
                int lastPeak; do { lastPeak = Volatile.Read(ref peak); if (now <= lastPeak) break; }
                while (Interlocked.CompareExchange(ref peak, now, lastPeak) != lastPeak);
                try
                {
                    await Task.Delay(80);
                    return Reply(Envelope(cfg.Protocol, Obj("subject",
                        ((string)input["subject"]).Length > 0 ? "SUBJ" : "",
                        "body", "[译" + section + "]").ToString()));
                }
                finally { Interlocked.Decrement(ref active); }
            };
            {
                var session = translator.CreateSession("Subject", longBody, cfg);
                var progress = new CaptureProgress();
                var translated = translator.TranslateAsync(session, CancellationToken.None, progress).GetAwaiter().GetResult();
                Check(Volatile.Read(ref peak) >= 2, "segments run concurrently (" + Volatile.Read(ref peak) + " simultaneous requests)");
                Check(translated.Body == string.Join("\n\n", Enumerable.Range(1, 12).Select(i => "[译" + i.ToString("D2") + "]")) &&
                    translated.Subject == "SUBJ", "parallel merge reproduces every segment in order");
                var fullSnapshot = progress.Find(session.TotalParts);
                Check(fullSnapshot != null && fullSnapshot.Snapshot.Body.Contains("[译12]"),
                    "progress carries a live merged snapshot");
            }
            {   // Partial snapshot mixes finished and pending segments deterministically:
                // section 02 resolves immediately while the rest are delayed.
                var longish = string.Join("\n\n", Enumerable.Range(1, 4)
                    .Select(i => "SECTION_" + i.ToString("D2") + " " + new string('x', 1000)));
                var partialSession = translator.CreateSession("S", longish, cfg);
                stub.Handle = async (request, ct) => {
                    var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    var input = JObject.Parse((string)payload["messages"][1]["content"]);
                    string section = Regex.Match((string)input["body"], "SECTION_(\\d+)").Groups[1].Value;
                    await Task.Delay(section == "02" ? 0 : 700);
                    return Reply(Envelope(cfg.Protocol, Obj("subject",
                        ((string)input["subject"]).Length > 0 ? "译文主题" : "",
                        "body", "[译" + section + "]").ToString()));
                };
                var progress2 = new CaptureProgress();
                var done = translator.TranslateAsync(partialSession, CancellationToken.None, progress2).GetAwaiter().GetResult();
                Check(done.Subject == "译文主题" && done.Body.Contains("[译02]") &&
                    new[] { 1, 3, 4 }.All(i => done.Body.Contains("[译" + i.ToString("D2") + "]")) &&
                    !done.Body.Contains("SECTION_"),
                    "parallel merge reproduces every partial-test segment");
                var mixed = progress2.Find(1);
                Check(mixed != null && mixed.Snapshot.Body.Contains("[译02]") &&
                    mixed.Snapshot.Body.Contains("SECTION_01") && !mixed.Snapshot.Body.Contains("[译01]"),
                    "snapshot after first completion mixes translated and original segments");
            }
            var resumeSession = translator.CreateSession("Subject", longBody, cfg);
            var attemptsBySection = new Dictionary<string, int>();
            object attemptsLock = new object();
            bool doomSection05 = true;
            stub.Handle = async (request, ct) => {
                var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                string bodyText = (string)input["body"];
                string section = Regex.Match(bodyText, "SECTION_(\\d+)").Groups[1].Value;
                lock (attemptsLock)
                {
                    if (!attemptsBySection.ContainsKey(section)) attemptsBySection[section] = 0;
                    attemptsBySection[section]++;
                }
                // SECTION_05 fails late so every sibling completes first and is preserved.
                await Task.Delay(section == "05" ? 900 : 0);
                if (section == "05" && doomSection05) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Reply(Envelope(cfg.Protocol, input.ToString()));
            };
            Throws<InvalidOperationException>(() => translator.TranslateAsync(resumeSession, CancellationToken.None), "later segment failure surfaces");
            Check(resumeSession.CompletedParts == resumeSession.TotalParts - 1 && attemptsBySection.ContainsKey("05") &&
                new[] { "01", "02", "03", "04" }.All(s => attemptsBySection.ContainsKey(s) && attemptsBySection[s] == 1),
                "completed segments survive a sibling failure");
            doomSection05 = false;
            cfg.TimeoutSeconds = 240;
            Check(resumeSession.MatchesConfiguration(cfg), "timeout can be raised without losing completed segments");
            var resumeProgress = new CaptureProgress();
            stub.Handle = async (request, ct) => {
                var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                string section = Regex.Match((string)input["body"], "SECTION_(\\d+)").Groups[1].Value;
                lock (attemptsLock) { if (!attemptsBySection.ContainsKey(section)) attemptsBySection[section] = 0; attemptsBySection[section]++; }
                return Reply(Envelope(cfg.Protocol, input.ToString()));
            };
            var resumed = translator.TranslateAsync(resumeSession, CancellationToken.None, resumeProgress).GetAwaiter().GetResult();
            Check(attemptsBySection.All(kv => kv.Value == (kv.Key == "05" ? 2 : 1)) &&
                resumed.Body.Contains("SECTION_01") && resumed.Body.Contains("SECTION_12"),
                "retry resumes only the failed segment");
            Check(resumeProgress.Last.PartTimeoutSeconds == 240 && resumeProgress.MaxCompleted == resumeSession.TotalParts,
                "larger configured timeout respected per segment");
            cfg.TimeoutSeconds = 8;

            // The first failure cancels in-flight siblings and queued work. Counts must come
            // from retained results, not total-minus-errors (cancelled units are not completed).
            {
                var stopped = translator.CreateSession("S", longBody, cfg);
                var threeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int started = 0, cancelled = 0;
                stub.Handle = async (req, ct) => {
                    int call = Interlocked.Increment(ref started);
                    if (call == 3) threeStarted.TrySetResult(true);
                    if (call == 1)
                    {
                        await threeStarted.Task;
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                    }
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                    return Reply("{}");
                };
                var stoppedTask = translator.TranslateAsync(stopped, CancellationToken.None);
                Check(Task.WhenAny(stoppedTask, Task.Delay(2500)).GetAwaiter().GetResult() == stoppedTask,
                    "first failure promptly stops in-flight and queued translation work");
                string error = "";
                try { stoppedTask.GetAwaiter().GetResult(); } catch (InvalidOperationException ex) { error = ex.Message; }
                Check(started == 3 && cancelled == 2, "failure cancels both siblings without starting queued requests");
                Check(stopped.CompletedParts == 0 && error.Contains("已完成 0 段"), "failure report counts only actual retained results");
                before = stub.Calls;
                stub.Handle = async (req, ct) => {
                    var payload = JObject.Parse(await req.Content.ReadAsStringAsync());
                    return Reply(Envelope(cfg.Protocol, (string)payload["messages"][1]["content"]));
                };
                translator.TranslateAsync(stopped, CancellationToken.None).GetAwaiter().GetResult();
                Check(stub.Calls - before == stopped.TotalParts && stopped.CompletedParts == stopped.TotalParts,
                    "retry includes failed, sibling-cancelled and never-started segments");
            }
            {
                var failures = new ConcurrentQueue<KeyValuePair<int, Exception>>();
                int started = 0;
                Func<int, CancellationToken, Task> hang = async (index, ct) => {
                    Interlocked.Increment(ref started);
                    await Task.Delay(Timeout.Infinite, ct);
                };
                var parallel = typeof(MailTranslationService).GetMethod("RunParallelAsync", BindingFlags.Static | BindingFlags.NonPublic);
                var task = (Task)parallel.Invoke(null, new object[] { Enumerable.Range(0, 8), 1, CancellationToken.None, failures, hang });
                Check(Task.WhenAny(task, Task.Delay(3000)).GetAwaiter().GetResult() == task,
                    "per-unit timeout also cancels queued and sibling requests");
                task.GetAwaiter().GetResult();
                Check(started == 3 && failures.Any(f => f.Value is TimeoutException), "timeout preserves the initiating failure without starting another batch");
            }

            // HTML in-place weaving with opaque inline placeholders: leaf blocks become units whose
            // template keeps full sentence context; markup/images/links survive.
            string wovenHtml = "<html><body><div class=\"box\" style=\"color:red\"><p>Hello <b>world</b></p>" +
                "<p><a href=\"http://example.com/x\">Read more</a></p><img src=\"http://example.com/p.png\">" +
                "<p>Last line</p></div></body></html>";
            var htmlLayout = HtmlMailLayout.Parse(wovenHtml);
            Check(htmlLayout.TotalUnits == 3 && htmlLayout.Texts[0].Contains("Hello") &&
                htmlLayout.Texts[0].Contains("\u27E60\u27E7world\u27E6/0\u27E7") &&
                htmlLayout.Texts[1].Contains("Read more") && htmlLayout.Texts[2].Contains("Last line"),
                "html layout builds a placeholder template per block");
            var nestedLayout = HtmlMailLayout.Parse(
                "<div><p><span>Dear</span> <span>Customer</span>,<b> your</b></p>" +
                "<p><span>order</span><a href=\"http://example.com\">status</a> <i>below</i></p></div>");
            Check(nestedLayout.TotalUnits == 2, "many inline spans collapse into paragraph-level units (" +
                nestedLayout.TotalUnits + " units)");
            Func<string, string> translateTemplate = t =>
                t.Replace("Read more", "阅读全文").Replace("Last line", "最后一行")
                 .Replace("world", "世界").Replace("Hello", "你好");
            stub.Handle = (req, ct) => {
                var payload = JObject.Parse(ReadString(req.Content));
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                return Task.FromResult(Reply(Envelope(cfg.Protocol,
                    Obj("subject", "译文主题", "body", translateTemplate((string)input["body"])).ToString())));
            };
            string wovenOut = translator.TranslateHtmlAsync(htmlLayout, "Original subject", cfg, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check(wovenOut.Contains("你好") && wovenOut.Contains("世界") && wovenOut.Contains("阅读全文") &&
                wovenOut.Contains("最后一行") && !wovenOut.Contains("Hello") && !wovenOut.Contains("Read more"),
                "html weaving replaces every block with its translation");
            Check(wovenOut.Contains("<b>") && wovenOut.Contains("</b>") &&
                wovenOut.Contains("http://example.com/x") &&
                wovenOut.Contains("http://example.com/p.png") && wovenOut.Contains("color:red") &&
                wovenOut.Contains("class=\"box\""), "html weaving preserves tags, links, images and inline styles");
            Check(htmlLayout.TranslatedSubject == "译文主题", "html weaving captures the translated subject");
            // Each inline element keeps its own translated text (placeholder content mapped back).
            Check(wovenOut.Contains("<b><span data-mp=\"0\" data-frag=\"1\">世界</span></b>") &&
                wovenOut.Contains("<a href=\"http://example.com/x\"><span data-mp=\"1\" data-frag=\"0\">阅读全文</span></a>"),
                "placeholder template maps back so inline elements retain translated text");
            // Progressive rebuild: the snapshot after the first unit contains a mix of both.
            stub.Handle = async (req, ct) => {
                var payload = JObject.Parse(ReadString(req.Content));
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                string template = (string)input["body"];
                await Task.Delay(template.Contains("Hello") ? 0 : 1200);
                return Reply(Envelope(cfg.Protocol, Obj("subject",
                    ((string)input["subject"]).Length > 0 ? "译文主题" : "",
                    "body", translateTemplate(template)).ToString()));
            };
            var htmlLayout2 = HtmlMailLayout.Parse(wovenHtml);
            var htmlProg = new CaptureHtmlProgress();
            translator.TranslateHtmlAsync(htmlLayout2, "S", cfg, CancellationToken.None, htmlProg).GetAwaiter().GetResult();
            var partialHtml = htmlProg.FindWhere(p => p.CompletedUnits == 1);
            Check(partialHtml != null && partialHtml.HtmlSnapshot.Contains("你好") &&
                partialHtml.HtmlSnapshot.Contains("Read more") && !partialHtml.HtmlSnapshot.Contains("阅读全文"),
                "first html unit completion yields a partially translated document");

            // Non-breaking spaces are decoded so whitespace-only blocks are excluded and the LLM
            // never sees or echoes literal &nbsp;.
            var nbspLayout = HtmlMailLayout.Parse("<p>Hello&nbsp;World</p><p>&nbsp;</p><p>&amp; &lt;3</p>");
            Check(!nbspLayout.Texts.Any(t => t.Contains("&nbsp;")) && nbspLayout.TotalUnits == 2 &&
                nbspLayout.Texts[0] == "Hello World" && nbspLayout.Texts[1] == "& <3",
                "nbsp entities decode and whitespace-only blocks are excluded from translation");
            stub.Handle = (req, ct) => {
                var payload = JObject.Parse(ReadString(req.Content));
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                return Task.FromResult(Reply(Envelope(cfg.Protocol,
                    Obj("subject", "", "body", "译" + (string)input["body"]).ToString())));
            };
            string nbspOut = translator.TranslateHtmlAsync(nbspLayout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
            Check(nbspOut.Contains("译Hello World") && nbspOut.Contains("译&") && !nbspOut.Contains("译&nbsp;"),
                "translated output contains no literal nbsp");
            // A model that drops placeholders degrades gracefully instead of failing the mail.
            var raggedLayout = HtmlMailLayout.Parse("<p>One <b>two</b> three</p>");
            stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol,
                Obj("subject", "", "body", "合并 译文").ToString())));
            string raggedOut = translator.TranslateHtmlAsync(raggedLayout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
            Check(raggedOut.Contains("合并 译文") && raggedOut.Contains("<b>"),
                "dropped placeholders fall back without breaking markup");
            // A well-formed placeholder template maps nested inline content correctly.
            var phLayout = HtmlMailLayout.Parse("<p>Click <a href=\"http://e.com\">here</a> to download.</p>");
            stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol,
                Obj("subject", "", "body", "点击\u27E60\u27E7此处\u27E6/0\u27E7下载。").ToString())));
            string phOut = translator.TranslateHtmlAsync(phLayout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
            Check(phOut.Contains("点击") && phOut.Contains("此处") && phOut.Contains("下载。") &&
                phOut.Contains("<a href=\"http://e.com\">") && !phOut.Contains("Click") && !phOut.Contains("here"),
                "well-formed placeholder template keeps sentence context and maps inline text");
            // alt/title/placeholder/aria-* attribute values are batched and translated in place.
            var attrLayout = HtmlMailLayout.Parse(
                "<img src=\"a.png\" alt=\"Profile picture\"><a title=\"Read more\">text</a>");
            Check(attrLayout.HasAttributes && attrLayout.AttributeCount == 2 && attrLayout.TotalJobs == 2,
                "alt/title attributes become batched attribute units");
            stub.Handle = async (req, ct) => {
                var payload = JObject.Parse(ReadString(req.Content));
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                if (input["attributes"] != null)
                {
                    var outAttrs = new JArray();
                    foreach (var a in (JArray)input["attributes"])
                        outAttrs.Add(Obj("id", (int)a["id"], "text",
                            ((string)a["text"]).Replace("Profile picture", "个人资料图片").Replace("Read more", "阅读全文")));
                    return Reply(Envelope(cfg.Protocol, Obj("attributes", outAttrs).ToString()));
                }
                return Reply(Envelope(cfg.Protocol, Obj("subject", "", "body",
                    ((string)input["body"]).Replace("text", "文字")).ToString()));
            };
            string attrOut = translator.TranslateHtmlAsync(attrLayout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
            Check(attrOut.Contains("alt=\"个人资料图片\"") && attrOut.Contains("title=\"阅读全文\"") &&
                attrOut.Contains("文字") && attrOut.Contains("<img src=\"a.png\""),
                "attribute values are translated in place and markup preserved");
            // Footnotes: a <p> with <br> line breaks, <sup> numbers and an inline <a> must be a
            // single unit so the surrounding text is not orphaned (regression for <br> in BlockTags).
            var footnoteLayout = HtmlMailLayout.Parse(
                "<p><sup>1&nbsp;&nbsp;</sup>Options <a href=\"http://e.com\">170 markets</a> text<br>" +
                "<sup>2&nbsp;&nbsp;</sup>Second footnote line.</p>");
            Check(footnoteLayout.TotalUnits == 1 && footnoteLayout.Texts[0].Contains("Options") &&
                footnoteLayout.Texts[0].Contains("170 markets") && footnoteLayout.Texts[0].Contains("Second footnote"),
                "footnote paragraph with br/sup/link stays one unit with all its text");

            // Mixed-content containers and bare HTML fragments must not lose text outside leaf blocks.
            foreach (string sample in new[] {
                "<div>BEFORE <b>BOLD</b><p>MIDDLE <a href='https://example.com'>LINK</a></p>AFTER</div>",
                "<table><tr><td>BEFORE<div>MIDDLE</div>AFTER</td></tr></table>",
                "BEFORE<br>MIDDLE<a>LINK</a>AFTER" })
            {
                var layout = HtmlMailLayout.Parse(sample);
                string all = string.Join("|", layout.Texts);
                Check(all.Contains("BEFORE") && all.Contains("MIDDLE") && all.Contains("AFTER"), "collect all mixed-container/root text runs");
                stub.Handle = (req, ct) => {
                    var payload = JObject.Parse(ReadString(req.Content));
                    var input = JObject.Parse((string)payload["messages"][1]["content"]);
                    return Task.FromResult(Reply(Envelope(cfg.Protocol, Obj("subject", "", "body",
                        ((string)input["body"]).Replace("BEFORE", "前文").Replace("MIDDLE", "中间").Replace("AFTER", "后文")).ToString())));
                };
                string output = translator.TranslateHtmlAsync(layout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
                Check(output.Contains("前文") && output.Contains("中间") && output.Contains("后文") &&
                    !output.Contains("BEFORE") && !output.Contains("AFTER"), "mixed HTML runs are translated in original positions");
            }
            var protectedLayout = HtmlMailLayout.Parse("<div>VISIBLE<p>ALSO_VISIBLE</p><script>SECRET_SCRIPT</script><span translate='no'>KEEP_ORIGINAL</span>AFTER</div>");
            Check(!string.Join("|", protectedLayout.Texts).Contains("SECRET_SCRIPT") &&
                !string.Join("|", protectedLayout.Texts).Contains("KEEP_ORIGINAL"), "mixed HTML collection respects skipped subtrees");

            // Attribute errors never mark the job complete; success is committed atomically.
            foreach (string failure in new[] { "http", "cancel", "{}", "{\"attributes\":[]}",
                "{\"attributes\":[{\"id\":0,\"text\":\"x\"},{\"id\":0,\"text\":\"y\"}]}",
                "{\"attributes\":[{\"id\":0,\"text\":\"x\"},{\"id\":1,\"text\":\"\"}]}" })
            {
                var layout = HtmlMailLayout.Parse("<p>BODY</p><img alt=\"ALT\" title=\"TITLE\">");
                stub.Handle = (req, ct) => {
                    var payload = JObject.Parse(ReadString(req.Content));
                    var input = JObject.Parse((string)payload["messages"][1]["content"]);
                    if (input["attributes"] == null) return Task.FromResult(Reply(Envelope(cfg.Protocol, input.ToString())));
                    if (failure == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                    if (failure == "cancel") { var cancelled = new TaskCompletionSource<HttpResponseMessage>(); cancelled.SetCanceled(); return cancelled.Task; }
                    return Task.FromResult(Reply(Envelope(cfg.Protocol, failure)));
                };
                bool failed = false;
                try { translator.TranslateHtmlAsync(layout, "", cfg, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { failed = ex is InvalidOperationException || ex is HttpRequestException; }
                Check(failed && !Get<bool>(layout, "AttributesDone") && layout.CompletedJobs == 1,
                    "failed/malformed attribute response remains unfinished: " + failure);
                var attrs = Get<System.Collections.IList>(layout, "Attributes");
                Check(attrs.Cast<object>().All(a => Get<string>(a, "Translated") == null), "invalid attribute batch commits no partial values");
                before = stub.Calls;
                stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol,
                    "{\"attributes\":[{\"id\":1,\"text\":\"标题\"},{\"id\":0,\"text\":\"图片\"}]}")));
                string output = translator.TranslateHtmlAsync(layout, "", cfg, CancellationToken.None).GetAwaiter().GetResult();
                Check(stub.Calls - before == 1 && layout.CompletedJobs == 2 && output.Contains("alt=\"图片\"") && output.Contains("title=\"标题\""),
                    "retry sends only unfinished attributes and accepts reordered valid IDs");
            }
            {
                var layout = HtmlMailLayout.Parse("<img alt='ALT'>");
                Check(layout.TotalUnits == 0 && layout.TotalJobs == 1, "attribute-only mail has a retryable translation job");
                stub.Handle = async (req, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); };
                using (var stop = new CancellationTokenSource(50))
                    Throws<OperationCanceledException>(() => translator.TranslateHtmlAsync(layout, "", cfg, stop.Token), "user cancellation propagates through attribute job");
                Check(!Get<bool>(layout, "AttributesDone") && layout.CompletedJobs == 0, "cancelled attributes are not marked complete");
            }

            var config = new ConfigService(); // Deliberately do not Load() or Save() real configuration.
            config.Current.Llms.Add(cfg);
            config.Current.LlmFallbackEnabled = false;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Theme.Apply(Theme.ParseMode(themeMode));
            var window = new MailCenterWindow(config);
            try
            {
                var source = SetMessage(window, "Original subject");
                stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol, TranslationJson("翻译主题"))));
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                Check(Get<TextBlock>(window, "_subject").Text == "翻译主题", "UI translation works with classifier fallback off");
                Check(ReferenceEquals(source, Get<MailMessageContent>(window, "_currentMessage")) && source.Subject == "Original subject", "source preserved for reply/extraction");
                Invoke(window, "ShowOriginalMessage");
                Check(Get<TextBlock>(window, "_subject").Text == "Original subject", "restore original");
                before = stub.Calls;
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                Check(stub.Calls == before && Get<TextBlock>(window, "_subject").Text == "翻译主题", "cached translation toggle avoids API request");

                Invoke(window, "ResetTranslation");
                var htmlMail = SetMessage(window, "Html subject");
                htmlMail.BodyHtml = "<p>Hi <img src=\"http://example.com/a.png\"><a href=\"http://example.com\">link</a></p>";
                stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol, TranslationJson("HTML译文"))));
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                Check(Get<TextBlock>(window, "_subject").Text == "HTML译文" &&
                    Get<WebBrowser>(window, "_htmlBody").Visibility == Visibility.Visible &&
                    Get<TextBox>(window, "_body").Visibility == Visibility.Collapsed,
                    "translated html mail renders the woven web view instead of plain text");
                Invoke(window, "ShowOriginalMessage");
                Check(Get<TextBox>(window, "_body").Visibility == Visibility.Collapsed &&
                    Get<WebBrowser>(window, "_htmlBody").Visibility == Visibility.Visible,
                    "original html mail still uses the web view");
                Invoke(window, "ShowTranslation", true);
                Check(Get<WebBrowser>(window, "_htmlBody").Visibility == Visibility.Visible,
                    "partial translation view also prefers the woven web document for html mail");

                // Exercise the actual UI patch loop with a controlled DOM sink. Requests complete
                // C -> attributes -> A -> B; neither a count nor an attribute job is a text index.
                Invoke(window, "ResetTranslation");
                var orderedLayout = HtmlMailLayout.Parse("<p>A</p><p>B</p><p>C</p><img alt='ALT'>");
                Set(window, "_htmlLayout", orderedLayout);
                var replies = new ConcurrentDictionary<string, TaskCompletionSource<HttpResponseMessage>>();
                foreach (string key in new[] { "A", "B", "C", "attributes" })
                    replies[key] = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                stub.Handle = (req, ct) => {
                    var payload = JObject.Parse(ReadString(req.Content));
                    var input = JObject.Parse((string)payload["messages"][1]["content"]);
                    return replies[input["attributes"] != null ? "attributes" : (string)input["body"]].Task;
                };
                var orderedTask = translator.TranslateHtmlAsync(orderedLayout, "", cfg, CancellationToken.None);
                var domText = new Dictionary<int, string>();
                var domAttrs = new Dictionary<int, string>();
                int patchCalls = 0;
                Func<int, int, string, bool> patchText = (i, k, text) => { domText[i] = text; patchCalls++; return true; };
                Func<int, string, string, bool> patchAttribute = (i, name, text) => { domAttrs[i] = text; return true; };
                replies["C"].SetResult(Reply(Envelope(cfg.Protocol, Obj("subject", "", "body", "C译文").ToString())));
                Pump(Task.Run(() => { if (!SpinWait.SpinUntil(() => orderedLayout.CompletedUnits == 1, 2000)) throw new Exception("C did not finish"); }));
                Invoke(window, "ApplyHtmlUnitDeltas", (Func<int, int, string, bool>)((i, k, text) => false), patchAttribute);
                Check(Get<HashSet<int>>(window, "_htmlAppliedUnits").Count == 0, "missing/loading DOM nodes are not marked applied");
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Check(domText.Count == 1 && domText[2] == "C译文" && patchCalls == 1,
                    "out-of-order unit is patched by ID once, not by completion count");
                replies["attributes"].SetResult(Reply(Envelope(cfg.Protocol, "{\"attributes\":[{\"id\":0,\"text\":\"图片\"}]}")));
                Pump(Task.Run(() => { if (!SpinWait.SpinUntil(() => orderedLayout.CompletedJobs == 2, 2000)) throw new Exception("Attributes did not finish"); }));
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Check(domAttrs[0] == "图片" && domText.Count == 1 && patchCalls == 1,
                    "late attributes patch independently without advancing a text index");
                replies["A"].SetResult(Reply(Envelope(cfg.Protocol, Obj("subject", "", "body", "A译文").ToString())));
                Pump(Task.Run(() => { if (!SpinWait.SpinUntil(() => orderedLayout.CompletedUnits == 2, 2000)) throw new Exception("A did not finish"); }));
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Check(domText.Count == 2 && domText[0] == "A译文" && !domText.ContainsKey(1), "late first unit is never skipped");
                replies["B"].SetResult(Reply(Envelope(cfg.Protocol, Obj("subject", "", "body", "B译文").ToString())));
                Pump(orderedTask);
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Check(domText.Count == 3 && domText[1] == "B译文" && patchCalls == 3,
                    "final UI delta applies every text unit regardless of completion order");
                string snapshot = (string)orderedLayout.GetType().GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(orderedLayout, null);
                Invoke(window, "RenderHtmlTranslation", snapshot, orderedLayout.CompletedJobs, orderedLayout.TotalJobs);
                Check(Get<HashSet<int>>(window, "_htmlAppliedUnits").Count == 0 &&
                    Get<HashSet<int>>(window, "_htmlAppliedAttributes").Count == 0, "navigation resets applied IDs rather than guessing from snapshot count");
                Invoke(window, "ApplyHtmlUnitDeltas", patchText, patchAttribute);
                Check(patchCalls == 6, "completed units can all be replayed after document navigation");

                // Verify the injected patch functions against the real WPF/IE document too.
                // This hidden host contains only local synthetic markup; no external resources.
                var browser = new WebBrowser();
                var browserHost = new Window { Content = browser, Width = 300, Height = 150,
                    Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    var loaded = new TaskCompletionSource<bool>();
                    browser.LoadCompleted += (s, e) => loaded.TrySetResult(true);
                    browserHost.Show();
                    var buildDocument = typeof(MailCenterWindow).GetMethod("BuildHtmlDocument", BindingFlags.Static | BindingFlags.NonPublic);
                    string document = (string)buildDocument.Invoke(null, new object[] {
                        "<p><span id='textTarget' data-mp='2' data-frag='0'>Original</span></p><img id='attributeTarget' data-mp-attr-0='' alt='Original'>" });
                    browser.NavigateToString(document);
                    Pump(loaded.Task);
                    Check(Equals(browser.InvokeScript("mpApply", new object[] { 2, 0, "正文译文" }), true), "real browser reports successful text patch");
                    Check(Equals(browser.InvokeScript("mpApply", new object[] { 99, 0, "missing" }), false), "real browser reports missing patch target");
                    Check(Equals(browser.InvokeScript("mpApplyAttribute", new object[] { 0, "alt", "图片译文" }), true), "real browser applies attribute patch");
                    Check((string)browser.InvokeScript("eval", new object[] { "document.getElementById('textTarget').textContent" }) == "正文译文" &&
                        (string)browser.InvokeScript("eval", new object[] { "document.getElementById('attributeTarget').getAttribute('alt')" }) == "图片译文",
                        "real browser DOM contains both text and attribute translations");
                }
                finally { browserHost.Close(); browser.Dispose(); }

                Invoke(window, "ClearPreview");
                Check(Get<MailTranslationSession>(window, "_translationSession") == null, "switching mail clears partial translation session");
                SetMessage(window, "Cancel me").Body = longBody;
                int uiRequests = 0;
                stub.Handle = async (req, ct) => {
                    if (++uiRequests > 1) { await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); }
                    var payload = JObject.Parse(await req.Content.ReadAsStringAsync());
                    return Reply(Envelope(cfg.Protocol, (string)payload["messages"][1]["content"]));
                };
                var pending = (Task)Invoke(window, "TranslateCurrentAsync");
                Check(Get<Button>(window, "_translate").Content is StackPanel && !Get<Button>(window, "_translate").IsHitTestVisible, "loading indicator and duplicate click guard");
                Check(Get<TextBlock>(window, "_translationInfo").Text.Contains("段") && Get<TextBlock>(window, "_translationInfo").Text.Contains("秒"), "UI shows segment progress and elapsed seconds");
                before = stub.Calls;
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                Check(stub.Calls == before, "duplicate invocation ignored");
                var uiSession = Get<MailTranslationSession>(window, "_translationSession");
                Pump(Task.Run(() => {
                    if (!SpinWait.SpinUntil(() => uiSession.CompletedParts == 1, 5000))
                        throw new Exception("First UI segment did not complete");
                }));
                Get<CancellationTokenSource>(window, "_translationCts").Cancel();
                Pump(pending);
                Check(Get<CancellationTokenSource>(window, "_translationCts") == null && Get<Button>(window, "_translate").IsHitTestVisible, "cancel restores controls");
                Check(Get<MailTranslationSession>(window, "_translationSession").CompletedParts == 1 &&
                    (string)Get<Button>(window, "_translate").Content == "继续翻译", "UI preserves completed chunks and offers resume after cancel");
                before = stub.Calls;
                stub.Handle = async (req, ct) => {
                    var payload = JObject.Parse(await req.Content.ReadAsStringAsync());
                    return Reply(Envelope(cfg.Protocol, (string)payload["messages"][1]["content"]));
                };
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                Check(stub.Calls - before == Get<MailTranslationSession>(window, "_translationSession").TotalParts - 1,
                    "UI resume sends only unfinished chunks");

                Invoke(window, "ClearPreview");
                SetMessage(window, "Stale request");
                var late = new TaskCompletionSource<HttpResponseMessage>();
                stub.Handle = (req, ct) => late.Task;
                pending = (Task)Invoke(window, "TranslateCurrentAsync");
                Invoke(window, "ClearPreview");
                Check(!Get<Button>(window, "_translate").IsEnabled, "clear selection disables translation");
                SetMessage(window, "Second mail");
                stub.Handle = (req, ct) => Task.FromResult(Reply(Envelope(cfg.Protocol, TranslationJson("第二封译文"))));
                Pump((Task)Invoke(window, "TranslateCurrentAsync"));
                late.SetResult(Reply(Envelope(cfg.Protocol, TranslationJson("过期译文"))));
                Pump(pending);
                Check(Get<TextBlock>(window, "_subject").Text == "第二封译文" && Get<MailTranslation>(window, "_translation").Subject == "第二封译文", "stale response cannot overwrite another mail");
                if (!string.IsNullOrEmpty(previewPath))
                {
                    var visual = (FrameworkElement)window.Content;
                    ((Grid)visual).Background = Theme.BgB;
                    visual.Measure(new Size(856, 556));
                    visual.Arrange(new Rect(0, 0, 856, 556));
                    visual.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(856, 556, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(previewPath)) encoder.Save(stream);
                    Check(Get<TextBox>(window, "_body").ActualHeight > 180, "minimum-size preview retains body space");
                }
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
            return Results.ToArray();
        }
        finally { httpField.SetValue(null, originalHttp); Logger.LogDir = originalLogDir; }
    }
}
'@
try { [MailTranslationSmokeTests]::Run($PreviewPath, $ThemeMode) }
catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
