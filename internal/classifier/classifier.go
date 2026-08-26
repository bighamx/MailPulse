package classifier

import (
	"regexp"
	"strings"

	"github.com/bighamx/MailPulse/internal/config"
)

type Result struct {
	Matched bool   `json:"matched"`
	Code    string `json:"code,omitempty"`
	URL     string `json:"url,omitempty"`
	Reason  string `json:"reason,omitempty"`
	Rule    string `json:"rule,omitempty"`
}

func Evaluate(subject, body, from string, rules []config.Rule) Result {
	subject = strings.ToLower(subject)
	body = strings.ToLower(body)
	from = strings.ToLower(from)
	for _, rule := range rules {
		if len(rule.SenderWhitelist) > 0 && !containsAny(from, rule.SenderWhitelist) {
			continue
		}
		subjectHit := len(rule.SubjectKeywords) > 0 && containsAny(subject, rule.SubjectKeywords)
		extraction := ""
		patternHit := false
		for _, expression := range rule.BodyPatterns {
			if strings.TrimSpace(expression) == "" {
				continue
			}
			matches, err := regexp.Compile(expression)
			if err != nil {
				continue
			}
			match := matches.FindStringSubmatch(body)
			if len(match) == 0 {
				continue
			}
			patternHit = true
			extraction = match[0]
			if len(match) > 1 && strings.TrimSpace(match[1]) != "" {
				extraction = match[1]
			}
			break
		}
		if !subjectHit && !patternHit {
			continue
		}
		result := Result{Matched: true, Rule: rule.Name}
		if rule.NotifyWithCode && extraction != "" {
			result.Code = strings.TrimSpace(extraction)
		}
		if rule.NotifyWithLink {
			match := regexp.MustCompile(`https?://[^\s"'<>]+`).FindString(body)
			if match != "" {
				result.URL = match
			}
		}
		if result.Code == "" && result.URL == "" && subjectHit {
			result.Reason = "Subject keyword matched: " + rule.Name
		} else {
			result.Reason = "Rule matched: " + rule.Name
		}
		return result
	}
	return Result{}
}

func containsAny(value string, terms []string) bool {
	for _, term := range terms {
		if term != "" && strings.Contains(value, strings.ToLower(term)) {
			return true
		}
	}
	return false
}
