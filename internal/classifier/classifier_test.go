package classifier

import (
	"testing"

	"github.com/bighamx/MailPulse/internal/config"
)

func TestEvaluateExtractsCode(t *testing.T) {
	rules := []config.Rule{{
		Name:            "code",
		SubjectKeywords: []string{"verification code"},
		BodyPatterns:    []string{`code is ([0-9]{6})`},
		NotifyWithCode:  true,
	}}
	got := Evaluate("Your verification code", "Your code is 123456.", "noreply@example.com", rules)
	if !got.Matched || got.Code != "123456" {
		t.Fatalf("got %#v", got)
	}
}

func TestEvaluateRespectsSenderWhitelist(t *testing.T) {
	rules := []config.Rule{{
		Name:            "code",
		SubjectKeywords: []string{"verification code"},
		SenderWhitelist: []string{"service@example.com"},
		BodyPatterns:    []string{`([0-9]{6})`},
		NotifyWithCode:  true,
	}}
	if got := Evaluate("verification code", "code 123456", "other@example.net", rules); got.Matched {
		t.Fatal("unexpected match")
	}
}
