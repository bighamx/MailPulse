package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

const BuiltInQwenModel = "qwen3.5:0.8b"

type Duration time.Duration

func (duration Duration) MarshalJSON() ([]byte, error) {
	return json.Marshal(time.Duration(duration))
}

func (duration *Duration) UnmarshalJSON(data []byte) error {
	if string(data) == "null" {
		*duration = 0
		return nil
	}
	if data[0] == '"' {
		var value string
		if err := json.Unmarshal(data, &value); err != nil {
			return err
		}
		parsed, err := time.ParseDuration(value)
		if err != nil {
			return err
		}
		*duration = Duration(parsed)
		return nil
	}
	var nanoseconds int64
	if err := json.Unmarshal(data, &nanoseconds); err != nil {
		return err
	}
	*duration = Duration(nanoseconds)
	return nil
}

type Account struct {
	ID                string    `json:"id"`
	Name              string    `json:"name"`
	Protocol          string    `json:"protocol"`
	Host              string    `json:"host"`
	Port              int       `json:"port"`
	UseSSL            bool      `json:"useSsl"`
	Username          string    `json:"username"`
	Password          string    `json:"password,omitempty"`
	UseOAuth          bool      `json:"useOauth"`
	OAuthUserEmail    string    `json:"oauthUserEmail,omitempty"`
	RefreshToken      string    `json:"refreshToken,omitempty"`
	Enabled           bool      `json:"enabled"`
	PollInterval      Duration  `json:"pollInterval"`
	MarkMatchedAsRead bool      `json:"markMatchedAsRead"`
	Status            string    `json:"status,omitempty"`
	LastError         string    `json:"lastError,omitempty"`
	LastCheckedAt     time.Time `json:"lastCheckedAt,omitempty"`
	LastMessageAt     time.Time `json:"lastMessageAt,omitempty"`
}

type Rule struct {
	Name            string   `json:"name"`
	SubjectKeywords []string `json:"subjectKeywords"`
	BodyPatterns    []string `json:"bodyPatterns"`
	SenderWhitelist []string `json:"senderWhitelist"`
	NotifyWithCode  bool     `json:"notifyWithCode"`
	NotifyWithLink  bool     `json:"notifyWithLink"`
}

type LLMProvider struct {
	ID            string    `json:"id"`
	Name          string    `json:"name"`
	Mode          string    `json:"mode"`     // api or local
	Protocol      string    `json:"protocol"` // openai-chat or anthropic
	Runtime       string    `json:"runtime"`  // ollama or llama.cpp
	BaseURL       string    `json:"baseUrl"`
	Model         string    `json:"model"`
	APIKey        string    `json:"apiKey,omitempty"`
	ModelPath     string    `json:"modelPath,omitempty"`
	Command       string    `json:"command,omitempty"`
	Timeout       Duration  `json:"timeout"`
	Enabled       bool      `json:"enabled"`
	AutoDownload  bool      `json:"autoDownload"`
	ContextTokens int       `json:"contextTokens"`
	MaxTokens     int       `json:"maxTokens"`
	Temperature   float64   `json:"temperature"`
	Status        string    `json:"status,omitempty"`
	LastError     string    `json:"lastError,omitempty"`
	LastTestedAt  time.Time `json:"lastTestedAt,omitempty"`
}

type AppConfig struct {
	Version              int           `json:"version"`
	Accounts             []Account     `json:"accounts"`
	Rules                []Rule        `json:"rules"`
	LLMFallbackEnabled   bool          `json:"llmFallbackEnabled"`
	ActiveLLMID          string        `json:"activeLlmId"`
	LLMProviders         []LLMProvider `json:"llmProviders"`
	LLMPrompt            string        `json:"llmPrompt"`
	AutoCopyCode         bool          `json:"autoCopyCode"`
	DesktopNotifications bool          `json:"desktopNotifications"`
	MarkReadAfterSeconds int           `json:"markReadAfterSeconds"`
	EventRetention       int           `json:"eventRetention"`
	Theme                string        `json:"theme"`
}

type Store struct {
	mu     sync.RWMutex
	path   string
	config AppConfig
}

func DefaultDir() string {
	if dir := os.Getenv("MAILPULSE_DATA_DIR"); dir != "" {
		return dir
	}
	if runtime.GOOS == "windows" {
		if dir := os.Getenv("APPDATA"); dir != "" {
			return filepath.Join(dir, "MailPulse")
		}
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return ".mailpulse"
	}
	return filepath.Join(home, ".local", "share", "MailPulse")
}

func DefaultPath() string { return filepath.Join(DefaultDir(), "config.json") }

func Default() AppConfig {
	local := LLMProvider{
		ID:            "builtin-qwen-local",
		Name:          "Qwen3.5-0.8B (local)",
		Mode:          "local",
		Protocol:      "openai-chat",
		Runtime:       "ollama",
		BaseURL:       "http://127.0.0.1:11434/v1",
		Model:         BuiltInQwenModel,
		Timeout:       Duration(60 * time.Second),
		Enabled:       true,
		AutoDownload:  true,
		ContextTokens: 4096,
		MaxTokens:     256,
		Temperature:   0.1,
	}
	api := LLMProvider{
		ID:            "builtin-qwen-api",
		Name:          "Qwen API",
		Mode:          "api",
		Protocol:      "openai-chat",
		Runtime:       "dashscope",
		BaseURL:       "https://dashscope.aliyuncs.com/compatible-mode/v1",
		Model:         "qwen3.5-0.8b",
		Timeout:       Duration(45 * time.Second),
		Enabled:       true,
		ContextTokens: 4096,
		MaxTokens:     256,
		Temperature:   0.1,
	}
	return AppConfig{
		Version:              3,
		Rules:                defaultRules(),
		LLMFallbackEnabled:   true,
		ActiveLLMID:          local.ID,
		LLMProviders:         []LLMProvider{local, api},
		LLMPrompt:            DefaultLLMPrompt,
		AutoCopyCode:         true,
		DesktopNotifications: true,
		MarkReadAfterSeconds: 0,
		EventRetention:       500,
		Theme:                "system",
	}
}

func defaultRules() []Rule {
	return []Rule{
		{
			Name:            "Verification code",
			SubjectKeywords: []string{"验证码", "校验码", "安全码", "verification code", "one-time code", "OTP", "passcode", "code"},
			BodyPatterns: []string{
				`(?:验证码|校验码|安全码|码|code|passcode)[^A-Za-z0-9]{0,16}([A-Za-z0-9]{4,12})`,
				`\b([A-Za-z0-9]{6,10})\b`,
			},
			NotifyWithCode: true,
		},
		{
			Name:            "Confirmation link",
			SubjectKeywords: []string{"激活", "确认", "verify", "confirm", "activate"},
			BodyPatterns: []string{
				`https?://[^\s"'<>]*(?:verify|confirm|activate|token=)[^\s"'<>]*`,
			},
			NotifyWithLink: true,
		},
	}
}

const DefaultLLMPrompt = `你是邮件验证码分类助手。判断邮件是否需要用户立即处理，并提取验证码或确认链接。
只输出 JSON，不要输出 Markdown 或解释。格式：
{"matched":true,"code":"验证码或空字符串","url":"确认链接或空字符串","reason":"一句话理由"}

邮件主题：{subject}
邮件正文：
{body}`

func Open(path string) (*Store, error) {
	if path == "" {
		path = DefaultPath()
	}
	if !filepath.IsAbs(path) {
		abs, err := filepath.Abs(path)
		if err != nil {
			return nil, err
		}
		path = abs
	}
	store := &Store{path: path, config: Default()}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		if err := store.Save(); err != nil {
			return nil, err
		}
		return store, nil
	}
	if err != nil {
		return nil, err
	}
	if len(data) != 0 {
		if err := json.Unmarshal(data, &store.config); err != nil {
			return nil, fmt.Errorf("parse config: %w", err)
		}
	}
	normalize(&store.config)
	if err := store.Save(); err != nil {
		return nil, err
	}
	return store, nil
}

func normalize(value *AppConfig) {
	if value.Version < 2 {
		fresh := Default()
		if len(value.Rules) == 0 {
			value.Rules = fresh.Rules
		}
		if len(value.LLMProviders) == 0 {
			value.LLMProviders = fresh.LLMProviders
		}
		if value.LLMPrompt == "" {
			value.LLMPrompt = fresh.LLMPrompt
		}
	}
	if value.Version < 3 {
		value.AutoCopyCode = true
		value.DesktopNotifications = true
	}
	value.Version = 3
	if value.ActiveLLMID == "" && len(value.LLMProviders) > 0 {
		value.ActiveLLMID = value.LLMProviders[0].ID
	}
	if value.EventRetention <= 0 {
		value.EventRetention = 500
	}
	for i := range value.Accounts {
		account := &value.Accounts[i]
		if account.ID == "" {
			account.ID = uuid.NewString()
		}
		if account.Protocol == "" {
			account.Protocol = "imap"
		}
		if account.Port == 0 {
			if strings.EqualFold(account.Protocol, "pop3") {
				account.Port = 995
			} else {
				account.Port = 993
			}
		}
		if account.PollInterval < Duration(5*time.Second) {
			account.PollInterval = Duration(45 * time.Second)
		}
		if account.Status == "" {
			account.Status = "idle"
		}
	}
	for i := range value.LLMProviders {
		provider := &value.LLMProviders[i]
		if provider.ID == "" {
			provider.ID = uuid.NewString()
		}
		if provider.Mode == "" {
			provider.Mode = "api"
		}
		if provider.Protocol == "" {
			provider.Protocol = "openai-chat"
		}
		if provider.Runtime == "" && provider.Mode == "local" {
			provider.Runtime = "ollama"
		}
		if provider.BaseURL == "" {
			if provider.Protocol == "anthropic" {
				provider.BaseURL = "https://api.anthropic.com/v1"
			} else if provider.Runtime == "ollama" && provider.Mode == "local" {
				provider.BaseURL = "http://127.0.0.1:11434/v1"
			} else {
				provider.BaseURL = "https://api.openai.com/v1"
			}
		}
		if provider.Model == "" && provider.Mode == "local" {
			provider.Model = BuiltInQwenModel
		}
		if provider.Timeout < Duration(time.Second) {
			provider.Timeout = Duration(45 * time.Second)
		}
		if provider.ContextTokens == 0 {
			provider.ContextTokens = 4096
		}
		if provider.MaxTokens == 0 {
			provider.MaxTokens = 256
		}
	}
}

func (s *Store) Get() AppConfig {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return clone(s.config)
}

func (s *Store) Update(next AppConfig) error {
	normalize(&next)
	s.mu.Lock()
	defer s.mu.Unlock()
	s.config = clone(next)
	return s.saveLocked()
}

func (s *Store) Mutate(fn func(*AppConfig)) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	fn(&s.config)
	normalize(&s.config)
	return s.saveLocked()
}

func (s *Store) Save() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.saveLocked()
}

func (s *Store) saveLocked() error {
	if err := os.MkdirAll(filepath.Dir(s.path), 0o700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(clone(s.config), "", "  ")
	if err != nil {
		return err
	}
	temp := s.path + ".tmp"
	if err := os.WriteFile(temp, data, 0o600); err != nil {
		return err
	}
	if err := os.Rename(temp, s.path); err != nil {
		return err
	}
	return nil
}

func clone(value AppConfig) AppConfig {
	data, _ := json.Marshal(value)
	var out AppConfig
	_ = json.Unmarshal(data, &out)
	return out
}

func Secret(value string) string {
	if strings.TrimSpace(value) == "" {
		return ""
	}
	return "configured"
}
