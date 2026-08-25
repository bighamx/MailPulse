using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace MailPulse.Services
{
    /// <summary>
    /// Microsoft OAuth2 device-code flow for consumer accounts (live.cn / live.com / outlook.com).
    /// Uses the well-known Outlook desktop client id, which is a public client and supports
    /// the device authorization grant with IMAP scopes.
    /// </summary>
    public static class MicrosoftOAuthService
    {
        // Well-known public "Microsoft Office" desktop client id (widely used by third-party mail clients)
        private const string ClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c";
        private const string Tenant = "consumers";   // personal Microsoft accounts only
        private const string Scope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access";

        private static readonly HttpClient Http = new HttpClient();

        public class DeviceCodeStart
        {
            public string UserCode { get; set; }
            public string VerificationUri { get; set; }
            public string DeviceCode { get; set; }
            public int IntervalSec { get; set; }
            public int ExpiresInSec { get; set; }
        }

        public class TokenResult
        {
            public bool Success { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public string Error { get; set; }
        }

        public static async Task<DeviceCodeStart> StartDeviceLoginAsync()
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
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
                    ["client_id"] = ClientId,
                    ["device_code"] = start.DeviceCode,
                });
                var resp = await Http.PostAsync(
                    $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content);
                var body = await resp.Content.ReadAsStringAsync();
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);

                string err = json.Value<string>("error");
                if (err == null)
                {
                    return new TokenResult
                    {
                        Success = true,
                        AccessToken = json.Value<string>("access_token"),
                        RefreshToken = json.Value<string>("refresh_token"),
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(json.Value<int?>("expires_in") ?? 3600),
                    };
                }
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { intervalMs += 2000; continue; }
                return new TokenResult { Success = false, Error = json.Value<string>("error_description") ?? err };
            }
            return new TokenResult { Success = false, Error = "登录超时，请重试。" };
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
        public static async Task<RefreshResult> RefreshAsync(string refreshToken)
        {
            try
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = ClientId,
                    ["refresh_token"] = refreshToken,
                    ["scope"] = Scope,
                });
                var resp = await Http.PostAsync(
                    $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content);
                var body = await resp.Content.ReadAsStringAsync();
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                string err = json.Value<string>("error");
                if (err != null)
                    return new RefreshResult { Success = false, Error = json.Value<string>("error_description") ?? err };
                return new RefreshResult
                {
                    Success = true,
                    AccessToken = json.Value<string>("access_token"),
                    NewRefreshToken = json.Value<string>("refresh_token"),
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(json.Value<int?>("expires_in") ?? 3600),
                };
            }
            catch (Exception ex) { return new RefreshResult { Success = false, Error = ex.Message }; }
        }
    }
}
