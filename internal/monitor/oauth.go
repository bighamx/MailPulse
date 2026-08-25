package monitor

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
)

const (
	microsoftClientID = "9e5f94bc-e8a4-4e73-b8be-63364c29d753"
	microsoftScope    = "https://outlook.office365.com/IMAP.AccessAsUser.All offline_access"
)

func microsoftTokenEndpoint() string {
	return "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
}

type DeviceLogin struct {
	DeviceCode      string `json:"deviceCode"`
	UserCode        string `json:"userCode"`
	VerificationURI string `json:"verificationUri"`
	ExpiresIn       int    `json:"expiresIn"`
	Interval        int    `json:"interval"`
}

type DeviceTokenResult struct {
	Pending      bool   `json:"pending"`
	AccessToken  string `json:"accessToken,omitempty"`
	RefreshToken string `json:"refreshToken,omitempty"`
	Error        string `json:"error,omitempty"`
}

func StartMicrosoftDeviceLogin(ctx context.Context) (DeviceLogin, error) {
	form := url.Values{"client_id": {microsoftClientID}, "scope": {microsoftScope}}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost,
		"https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
		strings.NewReader(form.Encode()))
	if err != nil {
		return DeviceLogin{}, err
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return DeviceLogin{}, err
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	var result struct {
		DeviceCode      string `json:"device_code"`
		UserCode        string `json:"user_code"`
		VerificationURI string `json:"verification_uri"`
		ExpiresIn       int    `json:"expires_in"`
		Interval        int    `json:"interval"`
		Error           string `json:"error"`
		Description     string `json:"error_description"`
	}
	if err := json.Unmarshal(body, &result); err != nil {
		return DeviceLogin{}, err
	}
	if result.Error != "" {
		if result.Description == "" {
			result.Description = result.Error
		}
		return DeviceLogin{}, errors.New(result.Description)
	}
	if result.DeviceCode == "" || result.UserCode == "" {
		return DeviceLogin{}, errors.New("Microsoft device-code response was incomplete")
	}
	return DeviceLogin{
		DeviceCode: result.DeviceCode, UserCode: result.UserCode, VerificationURI: result.VerificationURI,
		ExpiresIn: result.ExpiresIn, Interval: result.Interval,
	}, nil
}

func CompleteMicrosoftDeviceLogin(ctx context.Context, deviceCode string) (DeviceTokenResult, error) {
	form := url.Values{
		"grant_type":  {"urn:ietf:params:oauth:oauth2:v2:device_code"},
		"client_id":   {microsoftClientID},
		"device_code": {deviceCode},
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, microsoftTokenEndpoint(), strings.NewReader(form.Encode()))
	if err != nil {
		return DeviceTokenResult{}, err
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return DeviceTokenResult{}, err
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	var result struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		Error        string `json:"error"`
		Description  string `json:"error_description"`
	}
	if err := json.Unmarshal(body, &result); err != nil {
		return DeviceTokenResult{}, err
	}
	if result.Error == "authorization_pending" || result.Error == "slow_down" {
		return DeviceTokenResult{Pending: true}, nil
	}
	if result.Error != "" {
		if result.Description == "" {
			result.Description = result.Error
		}
		return DeviceTokenResult{Error: result.Description}, nil
	}
	if result.RefreshToken == "" {
		return DeviceTokenResult{Error: "Microsoft returned no refresh_token"}, nil
	}
	return DeviceTokenResult{AccessToken: result.AccessToken, RefreshToken: result.RefreshToken}, nil
}

func RefreshMicrosoftToken(ctx context.Context, refreshToken string) (string, error) {
	form := url.Values{
		"client_id":     {microsoftClientID},
		"grant_type":    {"refresh_token"},
		"refresh_token": {refreshToken},
		"scope":         {microsoftScope},
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, microsoftTokenEndpoint(), strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return "", fmt.Errorf("token endpoint returned %s: %s", response.Status, string(body))
	}
	var token struct {
		AccessToken string `json:"access_token"`
	}
	if err := json.Unmarshal(body, &token); err != nil {
		return "", err
	}
	if token.AccessToken == "" {
		return "", fmt.Errorf("token endpoint returned no access_token")
	}
	return token.AccessToken, nil
}
