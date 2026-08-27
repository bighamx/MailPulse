using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MailPulse.Services
{
    /// <summary>
    /// Microsoft OAuth2 device-code flow for consumer accounts (live.cn / live.com / outlook.com).
    /// Supports the legacy read-only public client and a user-supplied Entra public client.
    /// </summary>
    public static class MicrosoftOAuthService
    {
        // Legacy Microsoft public desktop client used by the original MailPulse read-only flow.
        private const string LegacyClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c";
        private const string Tenant = "consumers";   // personal Microsoft accounts only
        private const string ImapScope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access";
        private const string SmtpScope = "https://outlook.office.com/SMTP.Send offline_access";
        private const string GraphScope = "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send offline_access";
        private const string IdentityScope = " openid profile email";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly ConcurrentDictionary<string, CachedToken> TokenCache =
            new ConcurrentDictionary<string, CachedToken>();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> AuthenticationLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        private sealed class SemaphoreLease : IDisposable
        {
            private SemaphoreSlim _gate;
            public SemaphoreLease(SemaphoreSlim gate) { _gate = gate; }
            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _gate, null);
                if (gate != null) gate.Release();
            }
        }

        private class CachedToken
        {
            public string AccessToken { get; set; }
            public string NewRefreshToken { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public DateTime RetryAfterUtc { get; set; }
            public string Error { get; set; }
        }

        public class DeviceCodeStart
        {
            public string UserCode { get; set; }
            public string VerificationUri { get; set; }
            public string DeviceCode { get; set; }
            public string ClientId { get; set; }
            public int IntervalSec { get; set; }
            public int ExpiresInSec { get; set; }
        }

        public class TokenResult
        {
            public bool Success { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public string UserEmail { get; set; }
            public string Error { get; set; }
        }

        public static async Task<DeviceCodeStart> StartDeviceLoginAsync(string clientId)
        {
            bool customClient = !string.IsNullOrWhiteSpace(clientId);
            Guid parsedClientId;
            if (customClient && !Guid.TryParse(clientId, out parsedClientId))
                throw new Exception("请先填写有效的 Microsoft Entra 应用客户端 ID。");
            string effectiveClientId = customClient ? clientId.Trim() : LegacyClientId;
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = effectiveClientId,
                // The legacy Microsoft first-party client is pre-authorized only for
                // the Exchange IMAP scopes. Adding identity scopes makes consent fail
                // because users cannot grant new permissions to first-party apps.
                ["scope"] = customClient ? GraphScope + IdentityScope : ImapScope,
            });
            var resp = await Http.PostAsync(
                $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/devicecode", content);
            var body = await resp.Content.ReadAsStringAsync();
            var json = Newtonsoft.Json.Linq.JObject.Parse(body);
            if (body.Contains("error"))
                throw new Exception(json.Value<string>("error_description") ?? json.Value<string>("error"));
            return new DeviceCodeStart
            {
                DeviceCode = json.Value<string>("device_code"),
                ClientId = effectiveClientId,
                UserCode = json.Value<string>("user_code"),
                VerificationUri = json.Value<string>("verification_uri"),
                IntervalSec = json.Value<int?>("interval") ?? 5,
                ExpiresInSec = json.Value<int?>("expires_in") ?? 900,
            };
        }

        /// <summary>Polls token endpoint until user completes login or timeout.</summary>
        public static async Task<TokenResult> PollForTokenAsync(DeviceCodeStart start, Action<DeviceCodeStart> onStarted = null)
        {
            onStarted?.Invoke(start);
            int intervalMs = Math.Max(3000, start.IntervalSec * 1000);
            var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresInSec);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(intervalMs);
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = start.ClientId,
                    ["device_code"] = start.DeviceCode,
                });
                var resp = await Http.PostAsync(
                    $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content);
                var body = await resp.Content.ReadAsStringAsync();
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);

                string err = json.Value<string>("error");
                if (err == null)
                {
                    string userEmail = ReadIdentityEmail(json.Value<string>("id_token"));
                    return new TokenResult
                    {
                        Success = true,
                        AccessToken = json.Value<string>("access_token"),
                        RefreshToken = json.Value<string>("refresh_token"),
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(json.Value<int?>("expires_in") ?? 3600),
                        UserEmail = userEmail,
                    };
                }
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { intervalMs += 2000; continue; }
                return new TokenResult { Success = false, Error = json.Value<string>("error_description") ?? err };
            }
            return new TokenResult { Success = false, Error = "登录超时，请重试。" };
        }

        private static string ReadIdentityEmail(string idToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(idToken)) return "";
                string[] parts = idToken.Split('.');
                if (parts.Length < 2) return "";
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                string jsonText = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var claims = Newtonsoft.Json.Linq.JObject.Parse(jsonText);
                return claims.Value<string>("preferred_username") ?? claims.Value<string>("email") ?? "";
            }
            catch { return ""; }
        }

        public class RefreshResult
        {
            public bool Success { get; set; }
            public string AccessToken { get; set; }
            public string NewRefreshToken { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public string Error { get; set; }
        }

        /// <summary>Silently refresh access token using stored refresh token.</summary>
        public static void RememberAccessToken(string accountId, string accessToken, string refreshToken, DateTime expiresAtUtc, bool smtp = false, bool graph = false)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(accessToken)) return;
            var token = new CachedToken
            {
                AccessToken = accessToken,
                NewRefreshToken = refreshToken,
                ExpiresAtUtc = expiresAtUtc
            };
            TokenCache[accountId + (graph ? "|graph" : smtp ? "|smtp" : "|imap")] = token;
        }

        public static void RejectAccessToken(string accountId, bool smtp = false)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            CachedToken ignored;
            TokenCache.TryRemove(accountId + (smtp ? "|smtp" : "|imap"), out ignored);
        }

        /// <summary>Serializes Exchange authentication for one mailbox within the app process.</summary>
        public static async Task<IDisposable> EnterAuthenticationAsync(string accountId, CancellationToken token)
        {
            string key = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId;
            var gate = AuthenticationLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            return new SemaphoreLease(gate);
        }

        public static async Task<RefreshResult> RefreshAsync(string refreshToken, string clientId, string accountId, bool smtp = false, bool graph = false)
        {
            string accountKey = string.IsNullOrWhiteSpace(accountId) ? (clientId ?? "default") : accountId;
            string cacheKey = accountKey + (graph ? "|graph" : smtp ? "|smtp" : "|imap");
            CachedToken cached;
            if (TokenCache.TryGetValue(cacheKey, out cached))
            {
                if (!string.IsNullOrWhiteSpace(cached.AccessToken) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
                    return FromCache(cached);
                if (cached.RetryAfterUtc > DateTime.UtcNow)
                    return new RefreshResult { Success = false, Error = cached.Error };
            }

            // IMAP and SMTP refreshes share a lock because Microsoft may rotate the
            // same refresh token while either purpose requests a new access token.
            var gate = TokenLocks.GetOrAdd(accountKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (TokenCache.TryGetValue(cacheKey, out cached))
                {
                    if (!string.IsNullOrWhiteSpace(cached.AccessToken) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
                        return FromCache(cached);
                    if (cached.RetryAfterUtc > DateTime.UtcNow)
                        return new RefreshResult { Success = false, Error = cached.Error };
                }

                try
                {
                    bool customClient = !string.IsNullOrWhiteSpace(clientId);
                    Guid parsedClientId;
                    if (customClient && !Guid.TryParse(clientId, out parsedClientId))
                        return CacheFailure(cacheKey, "未配置有效的 Microsoft Entra 应用客户端 ID，请编辑账号并重新授权。");
                    string effectiveClientId = customClient ? clientId.Trim() : LegacyClientId;
                    var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = effectiveClientId,
                        ["refresh_token"] = refreshToken,
                        ["scope"] = graph ? GraphScope
                            : customClient && smtp ? SmtpScope
                            : ImapScope,
                    });
                    var resp = await Http.PostAsync(
                        $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content);
                    var body = await resp.Content.ReadAsStringAsync();
                    var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                    string err = json.Value<string>("error");
                    if (err != null)
                        return CacheFailure(cacheKey, json.Value<string>("error_description") ?? err);
                    cached = new CachedToken
                    {
                        AccessToken = json.Value<string>("access_token"),
                        NewRefreshToken = json.Value<string>("refresh_token"),
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(json.Value<int?>("expires_in") ?? 3600)
                    };
                    TokenCache[cacheKey] = cached;
                    return FromCache(cached);
                }
                catch (Exception ex) { return CacheFailure(cacheKey, ex.Message); }
            }
            finally { gate.Release(); }
        }

        private static RefreshResult FromCache(CachedToken cached)
        {
            return new RefreshResult
            {
                Success = true,
                AccessToken = cached.AccessToken,
                NewRefreshToken = cached.NewRefreshToken,
                ExpiresAtUtc = cached.ExpiresAtUtc
            };
        }

        private static RefreshResult CacheFailure(string cacheKey, string error)
        {
            TokenCache[cacheKey] = new CachedToken
            {
                Error = error,
                RetryAfterUtc = DateTime.UtcNow.AddSeconds(45)
            };
            return new RefreshResult { Success = false, Error = error };
        }
    }
}
