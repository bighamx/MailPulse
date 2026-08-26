package llm

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os/exec"
	"regexp"
	"strings"
	"time"

	"github.com/bighamx/MailPulse/internal/config"
)

type Classification struct {
	Matched bool   `json:"matched"`
	Code    string `json:"code"`
	URL     string `json:"url"`
	Reason  string `json:"reason"`
}

type Client struct {
	HTTP *http.Client
}

func New() *Client {
	return &Client{HTTP: &http.Client{}}
}

func (client *Client) Classify(ctx context.Context, provider config.LLMProvider, prompt, subject, body string) (Classification, error) {
	if !provider.Enabled {
		return Classification{}, errors.New("LLM provider is disabled")
	}
	requestCtx, cancel := context.WithTimeout(ctx, time.Duration(provider.Timeout))
	defer cancel()
	text, err := client.complete(requestCtx, provider, strings.NewReplacer("{subject}", subject, "{body}", body).Replace(prompt))
	if err != nil {
		return Classification{}, err
	}
	return parseClassification(text)
}

func (client *Client) Test(ctx context.Context, provider config.LLMProvider) error {
	result, err := client.Classify(ctx, provider, config.DefaultLLMPrompt, "Your verification code", "Your code is 654321.")
	if err != nil {
		return err
	}
	if !result.Matched {
		return fmt.Errorf("model returned an unexpected test result: %#v", result)
	}
	return nil
}

func (client *Client) complete(ctx context.Context, provider config.LLMProvider, prompt string) (string, error) {
	if provider.Protocol == "anthropic" {
		payload := map[string]any{
			"model":      provider.Model,
			"max_tokens": provider.MaxTokens,
			"messages": []map[string]any{{
				"role":    "user",
				"content": prompt,
			}},
		}
		var output struct {
			Content []struct {
				Text string `json:"text"`
			} `json:"content"`
		}
		if err := client.postJSON(ctx, provider, strings.TrimSuffix(provider.BaseURL, "/")+"/messages", payload, &output); err != nil {
			return "", err
		}
		if len(output.Content) == 0 {
			return "", errors.New("empty Anthropic response")
		}
		return output.Content[0].Text, nil
	}

	payload := map[string]any{
		"model": provider.Model,
		"messages": []map[string]any{
			{"role": "system", "content": "Return only compact JSON. Current date is " + time.Now().Format(time.DateOnly) + "."},
			{"role": "user", "content": prompt},
		},
		"temperature": provider.Temperature,
		"max_tokens":  provider.MaxTokens,
	}
	var output struct {
		Choices []struct {
			Message struct {
				Content string `json:"content"`
			} `json:"message"`
		} `json:"choices"`
	}
	if err := client.postJSON(ctx, provider, strings.TrimSuffix(provider.BaseURL, "/")+"/chat/completions", payload, &output); err != nil {
		return "", err
	}
	if len(output.Choices) == 0 || output.Choices[0].Message.Content == "" {
		return "", errors.New("empty OpenAI-compatible response")
	}
	return output.Choices[0].Message.Content, nil
}

func (client *Client) postJSON(ctx context.Context, provider config.LLMProvider, endpoint string, payload any, output any) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(data))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	if provider.Protocol == "anthropic" {
		request.Header.Set("x-api-key", provider.APIKey)
		request.Header.Set("anthropic-version", "2023-06-01")
	} else if provider.APIKey != "" {
		request.Header.Set("Authorization", "Bearer "+provider.APIKey)
	}
	response, err := client.HTTP.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	body, err := io.ReadAll(io.LimitReader(response.Body, 4<<20))
	if err != nil {
		return err
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		message := strings.TrimSpace(string(body))
		if len(message) > 500 {
			message = message[:500]
		}
		return fmt.Errorf("%s returned %s: %s", endpoint, response.Status, message)
	}
	if err := json.Unmarshal(body, output); err != nil {
		return fmt.Errorf("decode response: %w", err)
	}
	return nil
}

func parseClassification(text string) (Classification, error) {
	text = regexp.MustCompile(`(?s)<think>.*?</think>`).ReplaceAllString(text, "")
	text = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(text), "```json"))
	text = strings.TrimSpace(strings.TrimSuffix(text, "```"))
	start := strings.Index(text, "{")
	end := strings.LastIndex(text, "}")
	if start < 0 || end < start {
		return Classification{}, fmt.Errorf("model did not return JSON: %q", truncate(text, 300))
	}
	var result Classification
	if err := json.Unmarshal([]byte(text[start:end+1]), &result); err != nil {
		return Classification{}, fmt.Errorf("decode model JSON: %w", err)
	}
	return result, nil
}

func truncate(value string, length int) string {
	if len(value) <= length {
		return value
	}
	return value[:length] + "..."
}

func OllamaInstalled() bool {
	_, err := exec.LookPath("ollama")
	return err == nil
}

func OllamaHasModel(ctx context.Context, baseURL, model string) (bool, error) {
	tagsURL := strings.TrimSuffix(baseURL, "/")
	tagsURL = strings.TrimSuffix(tagsURL, "/v1") + "/api/tags"
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, tagsURL, nil)
	if err != nil {
		return false, err
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return false, err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return false, fmt.Errorf("ollama tags returned %s", response.Status)
	}
	var tags struct {
		Models []struct {
			Name string `json:"name"`
		} `json:"models"`
	}
	if err := json.NewDecoder(response.Body).Decode(&tags); err != nil {
		return false, err
	}
	for _, item := range tags.Models {
		if item.Name == model || strings.HasPrefix(item.Name, model+":") {
			return true, nil
		}
	}
	return false, nil
}

func PullOllamaModel(ctx context.Context, model string) error {
	command := exec.CommandContext(ctx, "ollama", "pull", model)
	output, err := command.CombinedOutput()
	if err != nil {
		message := strings.TrimSpace(string(output))
		if len(message) > 500 {
			message = message[:500]
		}
		return fmt.Errorf("ollama pull: %w: %s", err, message)
	}
	return nil
}
