using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MimeKit;

namespace MailPulse.Services
{
    public class MailMonitorService
    {
        private readonly ConfigService _config;
        private readonly ClassificationEngine _engine = new ClassificationEngine();
        private readonly SeenStore _seen = new SeenStore();
        private readonly LlmClassifier _llm = new LlmClassifier();

        // session-level uid dedupe (avoid re-fetching already-seen mails)
        private readonly ConcurrentDictionary<string, HashSet<string>> _seenUids =
            new ConcurrentDictionary<string, HashSet<string>>();

        // pending "mark as read" requests (accountId -> uid)
        private readonly ConcurrentQueue<Tuple<string, UniqueId>> _pendingMarkRead =
            new ConcurrentQueue<Tuple<string, UniqueId>>();

        private CancellationTokenSource _cts;
        public event Action<Models.ClassifyResult> OnNewMatchedMail;

        public MailMonitorService(ConfigService config)
        {
            _config = config;
            _seen.Load();
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            foreach (var acc in _config.Current.Accounts.Where(a => a.Enabled))
                Task.Run(() => MonitorAccountLoop(acc, _cts.Token), _cts.Token);
        }

        public void Stop() { try { _cts?.Cancel(); } catch { } _cts = null; }

        private async Task MonitorAccountLoop(Models.AccountConfig acc, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (acc.Protocol == Models.MailProtocol.Imap)
                        await ImapIdleAsync(acc, token);
                    else
                        await PollPop3Once(acc);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Services.Logger.Error("monitor loop error for " + acc.Name, ex); }
                if (token.IsCancellationRequested) break;
                try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task ImapIdleAsync(Models.AccountConfig acc, CancellationToken token)
        {
            using (var client = new ImapClient())
            {
                await client.ConnectAsync(acc.Host, acc.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, token);
                await AuthenticateImapAsync(client, acc, token);
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite, token);

                await CheckUnseenAsync(acc, inbox, token);
                await DrainMarkReadAsync(acc, inbox, token);

                if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    while (!token.IsCancellationRequested && client.IsConnected)
                    {
                        using (var done = new CancellationTokenSource(TimeSpan.FromMinutes(25)))
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, done.Token))
                        {
                            try { await client.IdleAsync(linked.Token); }
                            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; }
                            if (inbox.Count > 0)
                            {
                                await CheckUnseenAsync(acc, inbox, token);
                                await DrainMarkReadAsync(acc, inbox, token);
                            }
                        }
                    }
                }
                else
                {
                    while (!token.IsCancellationRequested && client.IsConnected)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, acc.PollIntervalSeconds)), token);
                        await CheckUnseenAsync(acc, inbox, token);
                        await DrainMarkReadAsync(acc, inbox, token);
                    }
                }
            }
        }

        private async Task CheckUnseenAsync(Models.AccountConfig acc, IMailFolder inbox, CancellationToken token)
        {
            var uids = await inbox.SearchAsync(SearchQuery.NotSeen, token);
            foreach (var uid in uids.TakeLast(20))
            {
                var seenSet = _seenUids.GetOrAdd(acc.Id, _ => new HashSet<string>());
                lock (seenSet)
                {
                    if (seenSet.Contains(uid.ToString())) continue;
                    seenSet.Add(uid.ToString());
                }
                MimeMessage msg;
                try { msg = await inbox.GetMessageAsync(uid, token); }
                catch { continue; }

                string key = acc.Id + "|" + (string.IsNullOrEmpty(msg.MessageId) ? uid.ToString() : msg.MessageId);
                if (_seen.Contains(key)) continue;   // already alerted in a previous run
                await ProcessMessageAsync(acc, msg, key, () =>
                {
                    // user clicked copy/ignore -> mark as read on the live IMAP session
                    _pendingMarkRead.Enqueue(Tuple.Create(acc.Id, uid));
                }, token);
            }
        }

        private async Task DrainMarkReadAsync(Models.AccountConfig acc, IMailFolder inbox, CancellationToken token)
        {
            var leftover = new List<Tuple<string, UniqueId>>();
            while (_pendingMarkRead.TryDequeue(out var item))
            {
                if (item.Item1 != acc.Id) { leftover.Add(item); continue; }
                try { await inbox.AddFlagsAsync(item.Item2, MessageFlags.Seen, false, token); }
                catch { /* retried on next drain */ }
            }
            foreach (var item in leftover) _pendingMarkRead.Enqueue(item);
        }

        private async Task PollPop3Once(Models.AccountConfig acc)
        {
            using (var client = new Pop3Client())
            {
                await client.ConnectAsync(acc.Host, acc.Port, MailKit.Security.SecureSocketOptions.SslOnConnect);
                await client.AuthenticateAsync(acc.User, SecureStore.Unprotect(acc.EncryptedPassword));
                int count = client.Count;
                var seenSet = _seenUids.GetOrAdd(acc.Id, _ => new HashSet<string>());
                var pending = new List<Tuple<MimeMessage, string>>();
                lock (seenSet)
                {
                    int start = Math.Max(0, count - 10);
                    for (int i = count - 1; i >= start; i--)
                    {
                        string uid = client.GetMessageUid(i);
                        if (seenSet.Contains(uid)) continue;
                        seenSet.Add(uid);
                        var msg = client.GetMessage(i);
                        string key = acc.Id + "|" + (string.IsNullOrEmpty(msg.MessageId) ? uid : msg.MessageId);
                        if (_seen.Contains(key)) continue;
                        pending.Add(Tuple.Create(msg, key));
                    }
                }
                foreach (var item in pending)
                    await ProcessMessageAsync(acc, item.Item1, item.Item2, null, CancellationToken.None);   // POP3 has no \Seen flag
            }
        }

        private async Task ProcessMessageAsync(Models.AccountConfig acc, MimeMessage msg, string seenKey, Action markRead, CancellationToken token)
        {
            string body = msg.TextBody ?? msg.HtmlBody ?? "";
            if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("<"))
                body = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
            string subject = msg.Subject ?? "";
            string from = msg.From?.ToString() ?? "";
            var r = _engine.Evaluate(subject, body, from, acc.Name, _config.Current.Rules);

            if (!r.Matched && _config.Current.LlmFallbackEnabled)
            {
                var cfg = LlmClassifier.FirstEnabled(_config.Current.Llms);
                if (cfg != null)
                {
                    var llm = await _llm.ClassifyAsync(subject, body, from, acc.Name, cfg, _config.Current.LlmPrompt, token);
                    if (llm.Matched)
                    {
                        llm.MarkAsRead = markRead;
                        r = llm;
                    }
                }
            }

            if (r.Matched)
            {
                _seen.Add(seenKey);   // persist: never re-alert this mail after restart
                r.MarkAsRead = markRead;
                if (_config.Current.AutoCopyCode && !string.IsNullOrEmpty(r.Code))
                    try { System.Windows.Clipboard.SetText(r.Code); } catch { }
                OnNewMatchedMail?.Invoke(r);
            }
        }

        private async Task AuthenticateImapAsync(ImapClient client, Models.AccountConfig acc, System.Threading.CancellationToken token)
        {
            if (acc.UseOAuth)
            {
                var refresh = SecureStore.Unprotect(acc.EncryptedRefreshToken);
                if (string.IsNullOrEmpty(refresh))
                    throw new Exception("OAuth refresh token missing; please re-login in settings.");
                var r = await MicrosoftOAuthService.RefreshAsync(refresh);
                if (!r.Success) throw new Exception("OAuth refresh failed: " + r.Error);
                if (!string.IsNullOrEmpty(r.NewRefreshToken))
                {
                    acc.EncryptedRefreshToken = SecureStore.Protect(r.NewRefreshToken);
                    _config.Save();
                }
                var sasl = new MailKit.Security.SaslMechanismOAuth2(
                    string.IsNullOrEmpty(acc.OAuthUserEmail) ? acc.User : acc.OAuthUserEmail,
                    r.AccessToken);
                await client.AuthenticateAsync(sasl, token);
            }
            else
            {
                await client.AuthenticateAsync(acc.User, SecureStore.Unprotect(acc.EncryptedPassword), token);
            }
        }
    }

    internal static class LinqExt
    {
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> src, int n)
        {
            var q = new Queue<T>();
            foreach (var x in src) { q.Enqueue(x); if (q.Count > n) q.Dequeue(); }
            return q.ToList();
        }
    }
}


