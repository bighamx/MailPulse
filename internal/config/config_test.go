package config

import (
	"encoding/json"
	"path/filepath"
	"testing"
	"time"
)

func TestOpenCreatesBuiltInQwenProviders(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	store, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	cfg := store.Get()
	if cfg.ActiveLLMID != "builtin-qwen-local" || len(cfg.LLMProviders) != 2 {
		t.Fatalf("unexpected default LLM configuration: %#v", cfg.LLMProviders)
	}
	if cfg.LLMProviders[0].Model != BuiltInQwenModel || cfg.LLMProviders[0].Mode != "local" {
		t.Fatalf("unexpected local provider: %#v", cfg.LLMProviders[0])
	}
	if !cfg.DesktopNotifications || !cfg.AutoCopyCode {
		t.Fatal("desktop notifications and automatic copying must be enabled by default")
	}
}

func TestDurationAcceptsStringAndNanoseconds(t *testing.T) {
	var value struct {
		First  Duration `json:"first"`
		Second Duration `json:"second"`
	}
	if err := json.Unmarshal([]byte(`{"first":"45s","second":60000000000}`), &value); err != nil {
		t.Fatal(err)
	}
	if value.First != Duration(45*time.Second) || value.Second != Duration(time.Minute) {
		t.Fatalf("unexpected durations: %#v", value)
	}
}

func TestNormalizePOP3Defaults(t *testing.T) {
	cfg := Default()
	cfg.Accounts = []Account{{Protocol: "pop3"}}
	normalize(&cfg)
	if cfg.Accounts[0].Port != 995 || cfg.Accounts[0].PollInterval != Duration(45*time.Second) {
		t.Fatalf("unexpected account defaults: %#v", cfg.Accounts[0])
	}
}
