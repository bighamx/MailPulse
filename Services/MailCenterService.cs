using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Newtonsoft.Json.Linq;

namespace MailPulse.Services
{
    public class MailListItem
    {
        public string Id { get; set; }
        public string Subject { get; set; }
        public string From { get; set; }
        public DateTimeOffset Date { get; set; }
        public bool IsUnread { get; set; }
        public string DateText => Date.LocalDateTime.ToString(Date.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "MM-dd HH:mm");
    }

    public class MailMessageContent
    {
        public string Subject { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Body { get; set; }
        public string BodyHtml { get; set; }
    }

    /// <summary>On-demand mail reader/sender used by the mail center window.</summary>
    public class MailCenterService : IDisposable
    {
        private readonly ConfigService _config;
        private static readonly HttpClient GraphHttp = new HttpClient();
        private readonly SemaphoreSlim _imapGate = new SemaphoreSlim(1, 1);
        private ImapClient _imapClient;
        private string _imapAccountId;

        public MailCenterService(ConfigService config) { _config = config; }

        public async Task<List<MailListItem>> LoadInboxAsync(Models.AccountConfig account, int limit, CancellationToken token)
        {
            if (IsGraphAccount(account)) return await LoadGraphInboxAsync(account, limit, token);
            if (account.Protocol == Models.MailProtocol.Imap)
            {
                await _imapGate.WaitAsync(token);
                try
                {
                    var client = await EnsureImapAsync(account, FolderAccess.ReadWrite, token);
                    var all = await client.Inbox.SearchAsync(SearchQuery.All, token);
                    var ids = all.Skip(Math.Max(0, all.Count - limit)).ToList();
                    var rows = await client.Inbox.FetchAsync(ids,
                        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                        MessageSummaryItems.Flags | MessageSummaryItems.InternalDate, token);
                    return rows.Select(x => new MailListItem
                    {
                        Id = x.UniqueId.ToString(),
                        Subject = TextEncodingRepair.Repair(x.Envelope?.Subject ?? "（无主题）"),
                        From = TextEncodingRepair.Repair(DisplayAddress(x.Envelope?.From)),
                        Date = x.InternalDate ?? x.Envelope?.Date ?? DateTimeOffset.MinValue,
                        IsUnread = !x.Flags.HasValue || !x.Flags.Value.HasFlag(MessageFlags.Seen)
                    }).OrderByDescending(x => x.Date).ToList();
                }
                catch { ResetImap(); throw; }
                finally { _imapGate.Release(); }
            }

            using (var client = new Pop3Client())
            {
                await client.ConnectAsync(account.Host, account.Port, IncomingSocketOptions(account), token);
                await client.AuthenticateAsync(account.User, SecureStore.Unprotect(account.EncryptedPassword), token);
                var result = new List<MailListItem>();
                int start = Math.Max(0, client.Count - limit);
                for (int i = client.Count - 1; i >= start; i--)
                {
                    token.ThrowIfCancellationRequested();
                    var headers = await client.GetMessageHeadersAsync(i, token);
                    string uid = await client.GetMessageUidAsync(i, token);
                    DateTimeOffset messageDate;
                    if (!DateTimeOffset.TryParse(headers[HeaderId.Date], out messageDate))
                        messageDate = DateTimeOffset.MinValue;
                    result.Add(new MailListItem
                    {
                        Id = uid,
                        Subject = TextEncodingRepair.Repair(headers[HeaderId.Subject] ?? "（无主题）"),
                        From = TextEncodingRepair.Repair(headers[HeaderId.From] ?? ""),
                        Date = messageDate,
                        IsUnread = false
                    });
                }
                await client.DisconnectAsync(true, token);
                return result;
            }
        }

        public async Task<MailMessageContent> LoadMessageAsync(Models.AccountConfig account, string id, CancellationToken token)
        {
            if (IsGraphAccount(account)) return await LoadGraphMessageAsync(account, id, token);
            if (account.Protocol == Models.MailProtocol.Imap)
            {
                await _imapGate.WaitAsync(token);
                try
                {
                    var client = await EnsureImapAsync(account, FolderAccess.ReadWrite, token);
                    var imapMessage = await client.Inbox.GetMessageAsync(UniqueId.Parse(id), token);
                    if (imapMessage == null) throw new Exception("邮件已不存在。");
                    string html = string.IsNullOrWhiteSpace(imapMessage.HtmlBody)
                        ? null : EmbedMimeInlineImages(TextEncodingRepair.Repair(imapMessage.HtmlBody), imapMessage);
                    string body = !string.IsNullOrWhiteSpace(imapMessage.TextBody)
                        ? TextEncodingRepair.Repair(imapMessage.TextBody).Trim() : StripHtml(html ?? "");
                    return new MailMessageContent
                    {
                        Subject = TextEncodingRepair.Repair(imapMessage.Subject ?? "（无主题）"),
                        From = TextEncodingRepair.Repair(imapMessage.From?.ToString() ?? ""),
                        To = TextEncodingRepair.Repair(imapMessage.To?.ToString() ?? ""),
                        Date = imapMessage.Date,
                        Body = body,
                        BodyHtml = html
                    };
                }
                catch { ResetImap(); throw; }
                finally { _imapGate.Release(); }
            }

            MimeMessage message;
            using (var client = new Pop3Client())
            {
                await client.ConnectAsync(account.Host, account.Port, IncomingSocketOptions(account), token);
                await client.AuthenticateAsync(account.User, SecureStore.Unprotect(account.EncryptedPassword), token);
                int index = -1;
                for (int i = client.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(await client.GetMessageUidAsync(i, token), id, StringComparison.Ordinal))
                    { index = i; break; }
                }
                if (index < 0) throw new Exception("邮件已不存在或邮箱内容已经变化。");
                message = await client.GetMessageAsync(index, token);
                await client.DisconnectAsync(true, token);
            }

            return new MailMessageContent
            {
                Subject = TextEncodingRepair.Repair(message.Subject ?? "（无主题）"),
                From = TextEncodingRepair.Repair(message.From?.ToString() ?? ""),
                To = TextEncodingRepair.Repair(message.To?.ToString() ?? ""),
                Date = message.Date,
                Body = ExtractReadableBody(message),
                BodyHtml = string.IsNullOrWhiteSpace(message.HtmlBody) ? null :
                    EmbedMimeInlineImages(TextEncodingRepair.Repair(message.HtmlBody), message)
            };
        }

        public async Task SendAsync(Models.AccountConfig account, string to, string cc, string subject, string body, CancellationToken token)
        {
            if (IsGraphAccount(account))
            {
                await SendGraphMailAsync(account, to, cc, subject, body, token);
                return;
            }
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(account.User));
            message.To.AddRange(InternetAddressList.Parse(to));
            if (!string.IsNullOrWhiteSpace(cc)) message.Cc.AddRange(InternetAddressList.Parse(cc));
            if (message.To.Count == 0) throw new Exception("请至少填写一个收件人。");
            message.Subject = subject ?? "";
            message.Body = new TextPart("plain") { Text = body ?? "" };

            string host = string.IsNullOrWhiteSpace(account.SmtpHost)
                ? Models.AccountConfig.GuessSmtpHost(account.Host, account.User)
                : account.SmtpHost.Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new Exception("未配置 SMTP 服务器，请编辑邮箱账号后填写。");
            int port = account.SmtpPort <= 0 ? 465 : account.SmtpPort;
            if (string.Equals(host, "smtp.office365.com", StringComparison.OrdinalIgnoreCase) && port == 465)
                port = 587; // Outlook uses explicit STARTTLS; port 465 fails during TLS negotiation.

            if (account.UseOAuth && (string.IsNullOrWhiteSpace(account.OAuthClientId) ||
                string.IsNullOrWhiteSpace(GetSmtpRefreshToken(account))))
                throw new Exception("尚未完成 Outlook 发送授权。请编辑账号，选择“自有 Entra 应用（单独用于发送）”并完成登录。");

            using (var client = new SmtpClient())
            {
                var socketOptions = !account.SmtpUseSsl ? SecureSocketOptions.None
                    : port == 587 ? SecureSocketOptions.StartTls
                    : port == 465 ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.Auto;
                await client.ConnectAsync(host, port, socketOptions, token);
                if (account.UseOAuth)
                {
                    string accessToken = await RefreshOAuthAsync(account, true);
                    try
                    {
                        await client.AuthenticateAsync(new SaslMechanismOAuth2(
                            string.IsNullOrWhiteSpace(account.OAuthUserEmail) ? account.User : account.OAuthUserEmail,
                            accessToken), token);
                    }
                    catch (MailKit.Security.AuthenticationException)
                    {
                        MicrosoftOAuthService.RejectAccessToken(account.Id, true);
                        throw;
                    }
                }
                else
                    await client.AuthenticateAsync(account.User, SecureStore.Unprotect(account.EncryptedPassword), token);
                await client.SendAsync(message, token);
                await client.DisconnectAsync(true, token);
            }
        }

        public Task MarkAsReadAsync(Models.AccountConfig account, string id, CancellationToken token)
        {
            return SetReadStateAsync(account, id, true, token);
        }

        public async Task SetReadStateAsync(Models.AccountConfig account, string id, bool isRead, CancellationToken token)
        {
            if (IsGraphAccount(account))
            {
                await SendGraphAsync(account, new HttpMethod("PATCH"), "me/messages/" + Uri.EscapeDataString(id),
                    new JObject { ["isRead"] = isRead }.ToString(), token);
                return;
            }
            if (account.Protocol != Models.MailProtocol.Imap)
                throw new Exception("POP3 协议不支持邮件已读状态。");
            await _imapGate.WaitAsync(token);
            try
            {
                var client = await EnsureImapAsync(account, FolderAccess.ReadWrite, token);
                if (isRead)
                    await client.Inbox.AddFlagsAsync(UniqueId.Parse(id), MessageFlags.Seen, true, token);
                else
                    await client.Inbox.RemoveFlagsAsync(UniqueId.Parse(id), MessageFlags.Seen, true, token);
            }
            catch { ResetImap(); throw; }
            finally { _imapGate.Release(); }
        }

        public async Task DeleteAsync(Models.AccountConfig account, string id, CancellationToken token)
        {
            if (IsGraphAccount(account))
            {
                await SendGraphAsync(account, HttpMethod.Delete, "me/messages/" + Uri.EscapeDataString(id), null, token);
                return;
            }
            if (account.Protocol == Models.MailProtocol.Imap)
            {
                await _imapGate.WaitAsync(token);
                try
                {
                    var client = await EnsureImapAsync(account, FolderAccess.ReadWrite, token);
                    await client.Inbox.AddFlagsAsync(UniqueId.Parse(id), MessageFlags.Deleted, true, token);
                    await client.Inbox.ExpungeAsync(token);
                }
                catch { ResetImap(); throw; }
                finally { _imapGate.Release(); }
                return;
            }

            using (var client = new Pop3Client())
            {
                await client.ConnectAsync(account.Host, account.Port, IncomingSocketOptions(account), token);
                await client.AuthenticateAsync(account.User, SecureStore.Unprotect(account.EncryptedPassword), token);
                int index = -1;
                for (int i = client.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(await client.GetMessageUidAsync(i, token), id, StringComparison.Ordinal))
                    { index = i; break; }
                }
                if (index < 0) throw new Exception("邮件已不存在或邮箱内容已经变化。");
                await client.DeleteMessageAsync(index, token);
                await client.DisconnectAsync(true, token);
            }
        }

        private async Task<ImapClient> EnsureImapAsync(Models.AccountConfig account, FolderAccess access, CancellationToken token)
        {
            if (_imapClient == null || !string.Equals(_imapAccountId, account.Id, StringComparison.Ordinal) || !_imapClient.IsConnected)
            {
                IDisposable authenticationLease = null;
                if (account.UseOAuth)
                    authenticationLease = await MicrosoftOAuthService.EnterAuthenticationAsync(account.Id, token);
                try
                {
                    Exception lastAuthenticationError = null;
                    for (int attempt = 1; attempt <= 5; attempt++)
                    {
                        ResetImap();
                        _imapClient = new ImapClient();
                        _imapAccountId = account.Id;
                        try
                        {
                            await _imapClient.ConnectAsync(account.Host, account.Port, IncomingSocketOptions(account), token);
                            await AuthenticateImapAsync(_imapClient, account, token);
                            lastAuthenticationError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            bool transientOAuthFailure = account.UseOAuth && IsAuthenticationFailure(ex);
                            ResetImap();
                            if (!transientOAuthFailure) throw;
                            lastAuthenticationError = ex;
                            if (attempt < 5)
                                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt + 1)), token);
                        }
                    }
                    if (lastAuthenticationError != null)
                    {
                        // Keep the same access token. Exchange's IMAP backend can reject a
                        // newly issued token briefly; refreshing again would restart that window.
                        throw new Exception("Outlook 连续 5 次拒绝 IMAP OAuth 认证。请等待约 30 秒后再次测试；若持续失败，请重新授权。最后一次错误：" + lastAuthenticationError.Message,
                            lastAuthenticationError);
                    }
                }
                finally { authenticationLease?.Dispose(); }
            }

            if (!_imapClient.Inbox.IsOpen)
                await _imapClient.Inbox.OpenAsync(access, token);
            else if (access == FolderAccess.ReadWrite && _imapClient.Inbox.Access != FolderAccess.ReadWrite)
            {
                await _imapClient.Inbox.CloseAsync(false, token);
                await _imapClient.Inbox.OpenAsync(FolderAccess.ReadWrite, token);
            }
            return _imapClient;
        }

        private void ResetImap()
        {
            var client = _imapClient;
            _imapClient = null;
            _imapAccountId = null;
            if (client == null) return;
            try { if (client.IsConnected) client.Disconnect(false); } catch { }
            try { client.Dispose(); } catch { }
        }

        private static SecureSocketOptions IncomingSocketOptions(Models.AccountConfig account)
        {
            return account.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
        }

        private static bool IsAuthenticationFailure(Exception ex)
        {
            while (ex != null)
            {
                if (ex is MailKit.Security.AuthenticationException) return true;
                ex = ex.InnerException;
            }
            return false;
        }

        public void Dispose()
        {
            ResetImap();
            _imapGate.Dispose();
        }

        private async Task AuthenticateImapAsync(ImapClient client, Models.AccountConfig account, CancellationToken token)
        {
            if (account.UseOAuth)
            {
                string accessToken = await RefreshOAuthAsync(account);
                try
                {
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(
                        string.IsNullOrWhiteSpace(account.OAuthUserEmail) ? account.User : account.OAuthUserEmail,
                        accessToken), token);
                }
                catch (MailKit.Security.AuthenticationException ex)
                {
                    throw new Exception("OAuth 令牌获取成功，但 Outlook 拒绝 IMAP 登录。请确认设备码登录的微软账号与邮箱地址一致，并在 Outlook 网页设置中启用 IMAP。服务器信息：" + ex.Message, ex);
                }
            }
            else
            {
                if (string.Equals(account.Host, "outlook.office365.com", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Outlook 已不接受当前密码认证方式。请编辑此账号，填写 Microsoft Entra 客户端 ID，并重新完成 OAuth 授权。");
                await client.AuthenticateAsync(account.User, SecureStore.Unprotect(account.EncryptedPassword), token);
            }
        }

        private async Task<string> RefreshOAuthAsync(Models.AccountConfig account, bool smtp = false)
        {
            string protectedRefresh = smtp ? GetSmtpRefreshToken(account) : GetImapRefreshToken(account);
            string refresh = SecureStore.Unprotect(protectedRefresh);
            if (string.IsNullOrWhiteSpace(refresh))
                throw new Exception(smtp
                    ? "尚未完成 Outlook 发送授权，请在账号设置中选择自有 Entra 方式登录。"
                    : "尚未完成 Outlook 快速读取授权，请在账号设置中选择快速登录并完成设备码授权。");
            string clientId = smtp ? account.OAuthClientId : "";
            var result = await MicrosoftOAuthService.RefreshAsync(refresh, clientId, account.Id, smtp);
            if (!result.Success) throw new Exception("OAuth 刷新失败：" + result.Error);
            if (!string.IsNullOrWhiteSpace(result.NewRefreshToken))
            {
                if (smtp) account.EncryptedSmtpRefreshToken = SecureStore.Protect(result.NewRefreshToken);
                else
                {
                    account.EncryptedImapRefreshToken = SecureStore.Protect(result.NewRefreshToken);
                    account.EncryptedRefreshToken = account.EncryptedImapRefreshToken;
                }
                _config.TrySave("OAuth refresh token");
            }
            return result.AccessToken;
        }

        public static bool IsGraphAccount(Models.AccountConfig account)
        {
            return account != null && account.UseOAuth &&
                !string.IsNullOrWhiteSpace(account.OAuthClientId) &&
                !string.IsNullOrWhiteSpace(account.EncryptedGraphRefreshToken);
        }

        private async Task<string> RefreshGraphTokenAsync(Models.AccountConfig account)
        {
            string refresh = SecureStore.Unprotect(account.EncryptedGraphRefreshToken);
            if (string.IsNullOrWhiteSpace(refresh))
                throw new Exception("Microsoft Graph 授权已失效，请编辑账号并重新登录。");
            var result = await MicrosoftOAuthService.RefreshAsync(refresh, account.OAuthClientId, account.Id, false, true);
            if (!result.Success) throw new Exception("Microsoft Graph OAuth 刷新失败：" + result.Error);
            if (!string.IsNullOrWhiteSpace(result.NewRefreshToken))
            {
                account.EncryptedGraphRefreshToken = SecureStore.Protect(result.NewRefreshToken);
                _config.TrySave("Graph refresh token");
            }
            return result.AccessToken;
        }

        private async Task<string> SendGraphAsync(Models.AccountConfig account, HttpMethod method, string relativeUrl, string json, CancellationToken token)
        {
            string accessToken = await RefreshGraphTokenAsync(account);
            using (var request = new HttpRequestMessage(method, "https://graph.microsoft.com/v1.0/" + relativeUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var response = await GraphHttp.SendAsync(request, token))
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = responseBody;
                        try { detail = JObject.Parse(responseBody)["error"]?["message"]?.ToString() ?? responseBody; } catch { }
                        throw new Exception("Microsoft Graph 请求失败 (" + (int)response.StatusCode + ")：" + detail);
                    }
                    return responseBody;
                }
            }
        }

        private async Task<List<MailListItem>> LoadGraphInboxAsync(Models.AccountConfig account, int limit, CancellationToken token)
        {
            string query = "me/mailFolders/inbox/messages?$top=" + Math.Max(1, limit) +
                "&$select=id,subject,from,receivedDateTime,isRead&$orderby=receivedDateTime%20desc";
            var json = JObject.Parse(await SendGraphAsync(account, HttpMethod.Get, query, null, token));
            return (json["value"] as JArray ?? new JArray()).Select(item =>
            {
                DateTimeOffset date;
                DateTimeOffset.TryParse(item.Value<string>("receivedDateTime"), out date);
                var address = item["from"]?["emailAddress"];
                string name = address?.Value<string>("name") ?? "";
                string email = address?.Value<string>("address") ?? "";
                return new MailListItem
                {
                    Id = item.Value<string>("id"),
                    Subject = TextEncodingRepair.Repair(item.Value<string>("subject") ?? "（无主题）"),
                    From = TextEncodingRepair.Repair(string.IsNullOrWhiteSpace(name) ? email : name),
                    Date = date,
                    IsUnread = !(item.Value<bool?>("isRead") ?? false)
                };
            }).ToList();
        }

        private async Task<MailMessageContent> LoadGraphMessageAsync(Models.AccountConfig account, string id, CancellationToken token)
        {
            string query = "me/messages/" + Uri.EscapeDataString(id) +
                "?$select=subject,from,toRecipients,receivedDateTime,body";
            var item = JObject.Parse(await SendGraphAsync(account, HttpMethod.Get, query, null, token));
            DateTimeOffset date;
            DateTimeOffset.TryParse(item.Value<string>("receivedDateTime"), out date);
            var fromAddress = item["from"]?["emailAddress"];
            string from = fromAddress?.Value<string>("address") ?? "";
            string fromName = fromAddress?.Value<string>("name") ?? "";
            if (!string.IsNullOrWhiteSpace(fromName)) from = fromName + " <" + from + ">";
            string to = string.Join(", ", (item["toRecipients"] as JArray ?? new JArray())
                .Select(x => x["emailAddress"]?.Value<string>("address") ?? "").Where(x => x.Length > 0));
            string content = TextEncodingRepair.Repair(item["body"]?.Value<string>("content") ?? "");
            bool isHtml = string.Equals(item["body"]?.Value<string>("contentType"), "html", StringComparison.OrdinalIgnoreCase);
            string html = isHtml ? await EmbedGraphInlineImagesAsync(account, id, content, token) : null;
            return new MailMessageContent
            {
                Subject = TextEncodingRepair.Repair(item.Value<string>("subject") ?? "（无主题）"),
                From = TextEncodingRepair.Repair(from),
                To = TextEncodingRepair.Repair(to),
                Date = date,
                Body = isHtml ? StripHtml(html) : content.Trim(),
                BodyHtml = html
            };
        }

        private async Task<string> EmbedGraphInlineImagesAsync(Models.AccountConfig account, string messageId,
            string html, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(html) || html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0)
                return html;
            string query = "me/messages/" + Uri.EscapeDataString(messageId) +
                "/attachments?$select=name,contentType,isInline,contentId,contentBytes";
            var json = JObject.Parse(await SendGraphAsync(account, HttpMethod.Get, query, null, token));
            foreach (var attachment in json["value"] as JArray ?? new JArray())
            {
                string contentId = (attachment.Value<string>("contentId") ?? "").Trim('<', '>');
                string bytes = attachment.Value<string>("contentBytes") ?? "";
                if (contentId.Length == 0 || bytes.Length == 0) continue;
                string contentType = attachment.Value<string>("contentType") ?? "application/octet-stream";
                html = ReplaceCid(html, contentId, "data:" + contentType + ";base64," + bytes);
            }
            return html;
        }

        private async Task SendGraphMailAsync(Models.AccountConfig account, string to, string cc, string subject, string body, CancellationToken token)
        {
            Func<string, JArray> recipients = value =>
            {
                var result = new JArray();
                if (string.IsNullOrWhiteSpace(value)) return result;
                InternetAddressList parsed;
                try { parsed = InternetAddressList.Parse(value.Replace(';', ',')); }
                catch (Exception ex) { throw new Exception("收件人地址格式不正确：" + ex.Message, ex); }
                foreach (var mailbox in parsed.Mailboxes)
                    result.Add(new JObject
                    {
                        ["emailAddress"] = new JObject
                        {
                            ["address"] = mailbox.Address,
                            ["name"] = mailbox.Name ?? ""
                        }
                    });
                return result;
            };
            var toRecipients = recipients(to);
            if (toRecipients.Count == 0) throw new Exception("请至少填写一个收件人。");
            var payload = new JObject
            {
                ["message"] = new JObject
                {
                    ["subject"] = subject ?? "",
                    ["body"] = new JObject { ["contentType"] = "Text", ["content"] = body ?? "" },
                    ["toRecipients"] = toRecipients,
                    ["ccRecipients"] = recipients(cc)
                },
                ["saveToSentItems"] = true
            };
            await SendGraphAsync(account, HttpMethod.Post, "me/sendMail", payload.ToString(), token);
        }

        private static string GetImapRefreshToken(Models.AccountConfig account)
        {
            if (!string.IsNullOrWhiteSpace(account.EncryptedImapRefreshToken)) return account.EncryptedImapRefreshToken;
            return string.IsNullOrWhiteSpace(account.OAuthClientId) ? account.EncryptedRefreshToken : "";
        }

        private static string GetSmtpRefreshToken(Models.AccountConfig account)
        {
            if (!string.IsNullOrWhiteSpace(account.EncryptedSmtpRefreshToken)) return account.EncryptedSmtpRefreshToken;
            return !string.IsNullOrWhiteSpace(account.OAuthClientId) ? account.EncryptedRefreshToken : "";
        }

        private static string DisplayAddress(InternetAddressList addresses)
        {
            if (addresses == null || addresses.Count == 0) return "";
            var mailbox = addresses.Mailboxes.FirstOrDefault();
            if (mailbox == null) return addresses.ToString();
            return string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name;
        }

        private static string ExtractReadableBody(MimeMessage message)
        {
            if (!string.IsNullOrWhiteSpace(message.TextBody)) return TextEncodingRepair.Repair(message.TextBody).Trim();
            return StripHtml(TextEncodingRepair.Repair(message.HtmlBody ?? ""));
        }

        private static string EmbedMimeInlineImages(string html, MimeMessage message)
        {
            if (string.IsNullOrWhiteSpace(html) || message == null ||
                html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0) return html;
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                string contentId = (part.ContentId ?? "").Trim('<', '>');
                if (contentId.Length == 0 || part.Content == null) continue;
                try
                {
                    using (var stream = new MemoryStream())
                    {
                        part.Content.DecodeTo(stream);
                        if (stream.Length == 0 || stream.Length > 15 * 1024 * 1024) continue;
                        string contentType = part.ContentType?.MimeType ?? "application/octet-stream";
                        string data = "data:" + contentType + ";base64," + Convert.ToBase64String(stream.ToArray());
                        html = ReplaceCid(html, contentId, data);
                    }
                }
                catch (Exception ex) { Logger.Warn("inline image decode failed: " + ex.Message); }
            }
            return html;
        }

        private static string ReplaceCid(string html, string contentId, string replacement)
        {
            return Regex.Replace(html, "cid:" + Regex.Escape(contentId),
                match => replacement, RegexOptions.IgnoreCase);
        }

        private static string StripHtml(string html)
        {
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>|<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<(br|p|div|li|tr|h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, "[ \\t]+", " ");
            html = Regex.Replace(html, "\\n\\s*\\n\\s*\\n+", "\n\n");
            return html.Trim();
        }
    }
}
