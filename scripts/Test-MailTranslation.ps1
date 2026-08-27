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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            Calls++;
            return Handle(request, token);
        }
    }
    static readonly List<string> Results = new List<string>();
    sealed class CaptureProgress : IProgress<MailTranslationProgress>
    {
        public MailTranslationProgress Last;
        public void Report(MailTranslationProgress value) { Last = value; }
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
    static T Get<T>(MailCenterWindow window, string name) { return (T)Field(name).GetValue(window); }
    static void Set(MailCenterWindow window, string name, object value) { Field(name).SetValue(window, value); }
    static object Invoke(MailCenterWindow window, string name)
    {
        return typeof(MailCenterWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
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
        var stub = new Stub();
        var clientType = typeof(LlmClassifier).Assembly.GetType("MailPulse.Services.LlmClient");
        var httpField = clientType.GetField("Http", BindingFlags.Static | BindingFlags.NonPublic);
        var originalHttp = httpField.GetValue(null);
        var fakeHttp = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan };
        httpField.SetValue(null, fakeHttp);
        var cfg = new LlmConfig { BaseUrl = "https://stub.invalid/v1", Model = "test-model", EncryptedApiKey = SecureStore.Protect("fake-key") };
        var translator = new MailTranslationService();
        try
        {
            foreach (LlmProtocol protocol in Enum.GetValues(typeof(LlmProtocol)))
            {
                cfg.Protocol = protocol;
                stub.Handle = async (request, token) => {
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
                stub.Handle = async (request, ct) => {
                    var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    var input = JObject.Parse(protocol == LlmProtocol.OpenAiResponses ? (string)payload["input"] :
                        (string)payload["messages"][protocol == LlmProtocol.OpenAiChat ? 1 : 0]["content"]);
                    chunkRequests.Add(input);
                    return Reply(Envelope(protocol, input.ToString()));
                };
                var session = translator.CreateSession("Subject", longBody, cfg);
                var progress = new CaptureProgress();
                var translated = translator.TranslateAsync(session, CancellationToken.None, progress).GetAwaiter().GetResult();
                Check(session.TotalParts > 1 && chunkRequests.Count == session.TotalParts, protocol + " long mail uses independent requests");
                Check(chunkRequests.All(p => ((string)p["body"]).Length <= 1800), protocol + " normal chunks bounded to 1800 characters");
                Check(chunkRequests.Skip(1).All(p => (string)p["subject"] == ""), protocol + " subject translated only once");
                Check(Enumerable.Range(1, 12).All(i => translated.Body.Contains("SECTION_" + i.ToString("D2"))), protocol + " all sections retained in order");
                Check(progress.Last.CompletedParts == session.TotalParts && progress.Last.PartTimeoutSeconds == 120, protocol + " per-part timeout independent of 8-second classifier setting");
            }
            cfg.Protocol = LlmProtocol.OpenAiChat;
            var resumeSession = translator.CreateSession("Subject", longBody, cfg);
            var attempts = new List<string>();
            bool failSecond = true;
            stub.Handle = async (request, ct) => {
                var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                var input = JObject.Parse((string)payload["messages"][1]["content"]);
                attempts.Add((string)input["body"]);
                if (attempts.Count == 2 && failSecond) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Reply(Envelope(cfg.Protocol, input.ToString()));
            };
            Throws<InvalidOperationException>(() => translator.TranslateAsync(resumeSession, CancellationToken.None), "later segment failure surfaces");
            Check(resumeSession.CompletedParts == 1, "completed segment survives a later failure");
            failSecond = false;
            cfg.TimeoutSeconds = 240;
            Check(resumeSession.MatchesConfiguration(cfg), "timeout can be raised without losing completed segments");
            var resumeProgress = new CaptureProgress();
            translator.TranslateAsync(resumeSession, CancellationToken.None, resumeProgress).GetAwaiter().GetResult();
            Check(attempts.Count == resumeSession.TotalParts + 1 && attempts[1] == attempts[2] && attempts.Skip(1).All(p => p != attempts[0]), "retry resumes failed segment without retranslating completed segment");
            Check(resumeProgress.Last.PartTimeoutSeconds == 240, "larger configured timeout respected per segment");
            cfg.TimeoutSeconds = 8;

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
        finally { httpField.SetValue(null, originalHttp); fakeHttp.Dispose(); }
    }
}
'@
[MailTranslationSmokeTests]::Run($PreviewPath, $ThemeMode)
