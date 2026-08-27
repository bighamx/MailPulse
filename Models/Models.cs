using System;
using System.Collections.Generic;

namespace MailPulse.Models
{
    public enum MailProtocol { Imap, Pop3 }

    public class AccountConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public MailProtocol Protocol { get; set; } = MailProtocol.Imap;
        public string Host { get; set; }
        public int Port { get; set; } = 993;
        public bool UseSsl { get; set; } = true;
        public string User { get; set; }

        // Outgoing mail. Empty host falls back to a provider/domain-based guess.
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 465;
        public bool SmtpUseSsl { get; set; } = true;

        // Basic auth (DPAPI-protected)
        public string EncryptedPassword { get; set; }
        // OAuth2 (Microsoft) - refresh token DPAPI-protected
        public bool UseOAuth { get; set; }
        public string EncryptedRefreshToken { get; set; }
        public string EncryptedImapRefreshToken { get; set; }
        public string EncryptedSmtpRefreshToken { get; set; }
        public string EncryptedGraphRefreshToken { get; set; }
        public string OAuthUserEmail { get; set; }   // upn used in SASL
        public string OAuthClientId { get; set; }    // public Entra app id; not a secret

        public bool Enabled { get; set; } = true;
        public int PollIntervalSeconds { get; set; } = 45;

        public static Dictionary<string, (string host, int imapPort, int popPort)> Presets =
            new Dictionary<string, (string, int, int)>
            {
                ["Gmail"]   = ("imap.gmail.com", 993, 995),
                ["QQ"]      = ("imap.qq.com", 993, 995),
                ["Outlook"] = ("outlook.office365.com", 993, 995),
            };

        public static string GuessSmtpHost(string incomingHost, string user)
        {
            string host = (incomingHost ?? "").ToLowerInvariant();
            string address = (user ?? "").ToLowerInvariant();
            if (host.Contains("gmail") || address.EndsWith("@gmail.com")) return "smtp.gmail.com";
            if (host.Contains("qq.com") || address.EndsWith("@qq.com")) return "smtp.qq.com";
            if (host.Contains("office365") || host.Contains("outlook") || address.EndsWith("@outlook.com") ||
                address.EndsWith("@hotmail.com") || address.EndsWith("@live.com") || address.EndsWith("@live.cn"))
                return "smtp.office365.com";
            if (host.StartsWith("imap.")) return "smtp." + host.Substring(5);
            if (host.StartsWith("pop.")) return "smtp." + host.Substring(4);
            if (host.StartsWith("pop3.")) return "smtp." + host.Substring(5);
            int at = address.LastIndexOf('@');
            return at >= 0 ? "smtp." + address.Substring(at + 1) : "";
        }
    }

    public enum LlmProtocol { OpenAiChat, OpenAiResponses, Anthropic }

    public class LlmConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "我的 LLM";
        public LlmProtocol Protocol { get; set; } = LlmProtocol.OpenAiChat;
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string Model { get; set; } = "gpt-4o-mini";
        public string EncryptedApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 8;
        public bool Enabled { get; set; } = true;

        public static string DefaultBaseUrl(LlmProtocol p)
        {
            switch (p)
            {
                case LlmProtocol.OpenAiResponses: return "https://api.openai.com/v1";
                case LlmProtocol.Anthropic: return "https://api.anthropic.com/v1";
                default: return "https://api.openai.com/v1";
            }
        }

        public static string DefaultModel(LlmProtocol p)
        {
            switch (p)
            {
                case LlmProtocol.OpenAiResponses: return "gpt-4o-mini";
                case LlmProtocol.Anthropic: return "claude-3-5-haiku-latest";
                default: return "gpt-4o-mini";
            }
        }
    }

    public class RuleConfig
    {
        public string Name { get; set; }
        public List<string> SubjectKeywords { get; set; } = new List<string>();
        public List<string> BodyPatterns { get; set; } = new List<string>();
        public List<string> SenderWhitelist { get; set; } = new List<string>();
        public bool NotifyWithCode { get; set; }
        public bool NotifyWithLink { get; set; }
    }

    public class AppConfig
    {
        public List<AccountConfig> Accounts { get; set; } = new List<AccountConfig>();
        public List<RuleConfig> Rules { get; set; } = new List<RuleConfig>(DefaultRules());
        public bool StartOnBoot { get; set; }
        public bool AutoCopyCode { get; set; } = true;
        public string ThemeMode { get; set; } = "Light";   // "Light" or "Dark"

        public bool LlmFallbackEnabled { get; set; }
        public List<LlmConfig> Llms { get; set; } = new List<LlmConfig>();
        public string LlmPrompt { get; set; } = DefaultLlmPrompt;

        public const string DefaultLlmPrompt =
            "你是邮件分类助手。根据给出的邮件主题与正文，判断它是否属于需要用户即时处理的验证码/确认类邮件，并提取验证码或确认链接。\n" +
            "只输出 JSON（不要代码块标记），格式：{\"is_urgent\": true或false, \"code\": \"提取到的验证码，没有则null\", \"url\": \"提取到的确认链接，没有则null\", \"reason\": \"一句话判断理由\"}\n\n" +
            "邮件主题：{subject}\n\n邮件正文：{body}";

        public static IEnumerable<RuleConfig> DefaultRules()
        {
            yield return new RuleConfig
            {
                Name = "验证码-数字",
                SubjectKeywords = new List<string> { "验证码", "verification code", "OTP", "安全码", "code" },
                BodyPatterns = new List<string>
                {
                    @"(?:验证码|校验码|码|code)[^A-Za-z0-9]{0,12}([A-Za-z0-9]{4,10})",
                    @"(?:码|code)[^0-9]{0,10}(\d{4,8})",
                    @"\b(\d{6})\b"
                },
                NotifyWithCode = true
            };
            yield return new RuleConfig
            {
                Name = "确认链接",
                SubjectKeywords = new List<string> { "激活", "确认", "verify", "confirm", "activate" },
                BodyPatterns = new List<string>
                {
                    @"https?://[^\s""<>]*(?:verify|confirm|activate|token=)[^\s""<>]*"
                },
                NotifyWithLink = true
            };
        }
    }

    public class ClassifyResult
    {
        public bool Matched { get; set; }
        public string Code { get; set; }
        public string Url { get; set; }
        public string Summary { get; set; }
        public string BodyPreview { get; set; }
        public string From { get; set; }
        public string AccountName { get; set; }
        public bool IsAiAgent { get; set; }
        /// <summary>Runtime callback to mark the source mail as read (IMAP only).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public Action MarkAsRead { get; set; }
    }
}




