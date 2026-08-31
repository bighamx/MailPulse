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
        private readonly ConcurrentDictionary<Task, byte> _messageTasks =
            new ConcurrentDictionary<Task, byte>();

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
            int consecutiveFailures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (MailCenterService.IsGraphAccount(acc))
                        await PollGraphOnce(acc, token);
                    else if (acc.Protocol == Models.MailProtocol.Imap)
                        await ImapIdleAsync(acc, token);
                    else
                        await PollPop3Once(acc);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Services.Logger.Error("monitor loop error for " + acc.Name, ex);
                }
                if (token.IsCancellationRequested) break;
                int delaySeconds = consecutiveFailures == 0
                    ? GetSuccessfulLoopDelaySeconds(acc)
                    : Math.Min(300, 15 * (1 << Math.Min(4, consecutiveFailures)));
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token); } catch (OperationCanceledException) { break; }
            }
        }

        internal static int GetSuccessfulLoopDelaySeconds(Models.AccountConfig account)
        {
            // IMAP IDLE stays inside ImapIdleAsync until disconnect/cancellation, so this is only
            // a short reconnect delay. Graph has no desktop push channel: a small poll interval is
            // the fastest reliable option without operating a public webhook service.
            if (MailCenterService.IsGraphAccount(account)) return 5;
            if (account != null && account.Protocol == Models.MailProtocol.Pop3)
                return Math.Max(15, account.PollIntervalSeconds);
            return 2;
        }

        private async Task ImapIdleAsync(Models.AccountConfig acc, CancellationToken token)
        {
            using (var client = new ImapClient())
            {
                IDisposable authenticationLease = null;
                if (acc.UseOAuth)
                    authenticationLease = await MicrosoftOAuthService.EnterAuthenticationAsync(acc.Id, token);
                try
                {
                    await client.ConnectAsync(acc.Host, acc.Port, acc.UseSsl
                        ? MailKit.Security.SecureSocketOptions.Auto
                        : MailKit.Security.SecureSocketOptions.None, token);
                    await AuthenticateImapAsync(client, acc, token);
                }
                finally { authenticationLease?.Dispose(); }
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite, token);

                await CheckUnseenAsync(acc, inbox, token);
                await DrainMarkReadAsync(acc, inbox, token);

                if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    while (!token.IsCancellationRequested && client.IsConnected)
                    {
                        using (var done = new CancellationTokenSource(TimeSpan.FromMinutes(25)))
                        {
                            // MailKit raises folder events while IdleAsync is still running; the
                            // method does not return just because EXISTS/EXPUNGE arrived. Cancel the
                            // current IDLE command from the event, then perform server operations.
                            EventHandler<EventArgs> countChanged = (s, e) =>
                            {
                                try { done.Cancel(); } catch (ObjectDisposedException) { }
                            };
                            inbox.CountChanged += countChanged;
                            try { await client.IdleAsync(done.Token, token); }
                            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; }
                            finally { inbox.CountChanged -= countChanged; }
                            if (client.IsConnected && inbox.Count > 0)
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
                QueueMessageProcessing(acc, msg, key, () =>
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
                await client.ConnectAsync(acc.Host, acc.Port, acc.UseSsl
                    ? MailKit.Security.SecureSocketOptions.Auto
                    : MailKit.Security.SecureSocketOptions.None);
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
                    QueueMessageProcessing(acc, item.Item1, item.Item2, null,
                        _cts == null ? CancellationToken.None : _cts.Token); // POP3 has no \Seen flag
            }
        }

        private async Task PollGraphOnce(Models.AccountConfig acc, CancellationToken token)
        {
            using (var service = new MailCenterService(_config))
            {
                var rows = await service.LoadInboxAsync(acc, 20, token);
                foreach (var row in rows.Where(x => x.IsUnread).Take(10))
                {
                    string key = acc.Id + "|graph|" + row.Id;
                    var seenSet = _seenUids.GetOrAdd(acc.Id, _ => new HashSet<string>());
                    lock (seenSet)
                    {
                        if (seenSet.Contains(row.Id)) continue;
                        seenSet.Add(row.Id);
                    }
                    if (_seen.Contains(key)) continue;
                    var content = await service.LoadMessageAsync(acc, row.Id, token);
                    var message = new MimeMessage { Subject = content.Subject ?? "" };
                    MailboxAddress mailbox;
                    if (MailboxAddress.TryParse(content.From, out mailbox)) message.From.Add(mailbox);
                    message.Body = new TextPart("plain") { Text = content.Body ?? "" };
                    string messageId = row.Id;
                    QueueMessageProcessing(acc, message, key, () => Task.Run(async () =>
                    {
                        try
                        {
                            using (var marker = new MailCenterService(_config))
                                await marker.MarkAsReadAsync(acc, messageId, CancellationToken.None);
                        }
                        catch { }
                    }), token);
                }
            }
        }

        private void QueueMessageProcessing(Models.AccountConfig account, MimeMessage message,
            string seenKey, Action markRead, CancellationToken token)
        {
            // Classification may call an LLM and take several seconds. Do not hold IMAP IDLE or
            // delay the next Graph poll while it runs; UID de-duplication already happened first.
            Task task = ProcessMessageAsync(account, message, seenKey, markRead, token);
            _messageTasks.TryAdd(task, 0);
            task.ContinueWith(completed =>
            {
                byte ignored;
                _messageTasks.TryRemove(completed, out ignored);
                if (completed.IsFaulted && completed.Exception != null)
                    Logger.Error("mail classification failed for " + account.Name,
                        completed.Exception.GetBaseException());
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task ProcessMessageAsync(Models.AccountConfig acc, MimeMessage msg, string seenKey, Action markRead, CancellationToken token)
        {
            string body = TextEncodingRepair.Repair(msg.TextBody ?? msg.HtmlBody ?? "");
            if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("<"))
                body = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
            string subject = TextEncodingRepair.Repair(msg.Subject ?? "");
            string from = TextEncodingRepair.Repair(msg.From?.ToString() ?? "");
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
                r.BodyPreview = CreateBodyPreview(body);
                _seen.Add(seenKey);   // persist: never re-alert this mail after restart
                r.MarkAsRead = markRead;
                if (_config.Current.AutoCopyCode && !string.IsNullOrEmpty(r.Code))
                    try { System.Windows.Clipboard.SetText(r.Code); } catch { }
                OnNewMatchedMail?.Invoke(r);
            }
        }

        internal static string CreateBodyPreview(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            string preview = System.Net.WebUtility.HtmlDecode(body);
            preview = System.Text.RegularExpressions.Regex.Replace(preview, @"\s+", " ").Trim();
            const int maxLength = 480;
            return preview.Length <= maxLength ? preview : preview.Substring(0, maxLength).TrimEnd() + "…";
        }

        private async Task AuthenticateImapAsync(ImapClient client, Models.AccountConfig acc, System.Threading.CancellationToken token)
        {
            if (acc.UseOAuth)
            {
                string protectedRefresh = !string.IsNullOrWhiteSpace(acc.EncryptedImapRefreshToken)
                    ? acc.EncryptedImapRefreshToken
                    : (string.IsNullOrWhiteSpace(acc.OAuthClientId) ? acc.EncryptedRefreshToken : "");
                var refresh = SecureStore.Unprotect(protectedRefresh);
                if (string.IsNullOrEmpty(refresh))
                    throw new Exception("Outlook quick-read authorization is missing; select quick login in account settings and authorize reading.");
                var r = await MicrosoftOAuthService.RefreshAsync(refresh, "", acc.Id);
                if (!r.Success) throw new Exception("OAuth refresh failed: " + r.Error);
                if (!string.IsNullOrEmpty(r.NewRefreshToken))
                {
                    acc.EncryptedImapRefreshToken = SecureStore.Protect(r.NewRefreshToken);
                    acc.EncryptedRefreshToken = acc.EncryptedImapRefreshToken;
                    _config.TrySave("monitor OAuth refresh token");
                }
                var sasl = new MailKit.Security.SaslMechanismOAuth2(
                    string.IsNullOrEmpty(acc.OAuthUserEmail) ? acc.User : acc.OAuthUserEmail,
                    r.AccessToken);
                try { await client.AuthenticateAsync(sasl, token); }
                catch (MailKit.Security.AuthenticationException ex)
                {
                    throw new Exception("OAuth token was accepted by Entra, but Outlook rejected IMAP authentication. Verify the authorized mailbox identity and that IMAP is enabled. " + ex.Message, ex);
                }
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


