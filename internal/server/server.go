package server

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"net"
	"net/http"
	"strings"
	"time"

	"github.com/bighamx/MailPulse/internal/classifier"
	"github.com/bighamx/MailPulse/internal/config"
	"github.com/bighamx/MailPulse/internal/events"
	"github.com/bighamx/MailPulse/internal/llm"
	"github.com/bighamx/MailPulse/internal/monitor"
	"github.com/bighamx/MailPulse/internal/notify"
	"github.com/bighamx/MailPulse/web"
)

const keepSecret = "__KEEP__"
const clearSecret = "__CLEAR__"

type Server struct {
	ctx     context.Context
	store   *config.Store
	bus     *events.Bus
	monitor *monitor.Monitor
	logger  *slog.Logger
	started time.Time
	http    *http.Server
}

func New(ctx context.Context, address string, store *config.Store, bus *events.Bus, mailMonitor *monitor.Monitor, logger *slog.Logger) *Server {
	server := &Server{ctx: ctx, store: store, bus: bus, monitor: mailMonitor, logger: logger, started: time.Now().UTC()}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/health", server.health)
	mux.HandleFunc("GET /api/config", server.getConfig)
	mux.HandleFunc("PUT /api/config", server.putConfig)
	mux.HandleFunc("GET /api/events", server.getEvents)
	mux.HandleFunc("DELETE /api/events", server.clearEvents)
	mux.HandleFunc("GET /api/stream", server.stream)
	mux.HandleFunc("POST /api/accounts/test", server.testAccount)
	mux.HandleFunc("POST /api/llm/test", server.testLLM)
	mux.HandleFunc("POST /api/llm/install", server.installLLM)
	mux.HandleFunc("POST /api/notifications/test", server.testNotification)
	mux.HandleFunc("POST /api/classify/test", server.testClassifier)
	mux.HandleFunc("POST /api/events/{id}/read", server.markEventRead)
	mux.HandleFunc("POST /api/oauth/microsoft/start", server.startMicrosoftOAuth)
	mux.HandleFunc("POST /api/oauth/microsoft/exchange", server.exchangeMicrosoftOAuth)

	static, err := fs.Sub(web.Static, "static")
	if err != nil {
		logger.Error("embed web ui failed", "error", err)
	} else {
		mux.Handle("/", http.FileServerFS(static))
	}
	server.http = &http.Server{
		Addr: address, Handler: logRequests(logger, mux), ReadHeaderTimeout: 10 * time.Second,
	}
	return server
}

func (server *Server) ListenAndServe() error {
	listener, err := net.Listen("tcp", server.http.Addr)
	if err != nil {
		return err
	}
	server.logger.Info("web ui listening", "address", "http://"+listener.Addr().String())
	if err := server.http.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}

func (server *Server) Shutdown(ctx context.Context) error {
	return server.http.Shutdown(ctx)
}

func logRequests(logger *slog.Logger, next http.Handler) http.Handler {
	return http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		next.ServeHTTP(writer, request)
		if !strings.HasPrefix(request.URL.Path, "/api/stream") {
			logger.Debug("http request", "method", request.Method, "path", request.URL.Path)
		}
	})
}

func (server *Server) health(writer http.ResponseWriter, request *http.Request) {
	writeJSON(writer, http.StatusOK, map[string]any{
		"status": "ok", "version": "2.0.0", "uptime": time.Since(server.started).String(),
		"dataDir": config.DefaultDir(),
	})
}

func (server *Server) getConfig(writer http.ResponseWriter, request *http.Request) {
	cfg := server.store.Get()
	for index := range cfg.Accounts {
		cfg.Accounts[index].Password = secretValue(cfg.Accounts[index].Password)
		cfg.Accounts[index].RefreshToken = secretValue(cfg.Accounts[index].RefreshToken)
	}
	for index := range cfg.LLMProviders {
		cfg.LLMProviders[index].APIKey = secretValue(cfg.LLMProviders[index].APIKey)
	}
	writeJSON(writer, http.StatusOK, cfg)
}

func (server *Server) putConfig(writer http.ResponseWriter, request *http.Request) {
	var next config.AppConfig
	if !decodeBody(writer, request, &next) {
		return
	}
	server.mergeSecrets(&next)
	if err := server.store.Update(next); err != nil {
		writeError(writer, http.StatusInternalServerError, err)
		return
	}
	server.monitor.Restart(server.ctx)
	server.getConfig(writer, request)
}

func (server *Server) mergeSecrets(next *config.AppConfig) {
	current := server.store.Get()
	for index := range next.Accounts {
		account := &next.Accounts[index]
		if previous, ok := findAccount(current, account.ID); ok {
			account.Password = mergeSecret(account.Password, previous.Password)
			account.RefreshToken = mergeSecret(account.RefreshToken, previous.RefreshToken)
		} else {
			account.Password = normalizeSubmittedSecret(account.Password)
			account.RefreshToken = normalizeSubmittedSecret(account.RefreshToken)
		}
	}
	for index := range next.LLMProviders {
		provider := &next.LLMProviders[index]
		if previous, ok := findLLM(current, provider.ID); ok {
			provider.APIKey = mergeSecret(provider.APIKey, previous.APIKey)
		} else {
			provider.APIKey = normalizeSubmittedSecret(provider.APIKey)
		}
	}
}

func (server *Server) getEvents(writer http.ResponseWriter, request *http.Request) {
	writeJSON(writer, http.StatusOK, map[string]any{"events": server.bus.Recent(200)})
}

func (server *Server) clearEvents(writer http.ResponseWriter, request *http.Request) {
	server.bus.Clear()
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true})
}

func (server *Server) stream(writer http.ResponseWriter, request *http.Request) {
	flusher, ok := writer.(http.Flusher)
	if !ok {
		writeError(writer, http.StatusInternalServerError, errors.New("streaming unsupported"))
		return
	}
	_, channel, unsubscribe := server.bus.Subscribe()
	defer unsubscribe()
	writer.Header().Set("Content-Type", "text/event-stream")
	writer.Header().Set("Cache-Control", "no-cache")
	writer.Header().Set("Connection", "keep-alive")
	fmt.Fprint(writer, "retry: 3000\n\n")
	flusher.Flush()
	heartbeat := time.NewTicker(20 * time.Second)
	defer heartbeat.Stop()
	for {
		select {
		case <-request.Context().Done():
			return
		case event := <-channel:
			data, _ := json.Marshal(event)
			fmt.Fprintf(writer, "id: %s\nevent: %s\ndata: %s\n\n", event.ID, event.Kind, data)
			flusher.Flush()
		case <-heartbeat.C:
			fmt.Fprint(writer, ": ping\n\n")
			flusher.Flush()
		}
	}
}

func (server *Server) testAccount(writer http.ResponseWriter, request *http.Request) {
	var account config.Account
	if !decodeBody(writer, request, &account) {
		return
	}
	if previous, ok := findAccount(server.store.Get(), account.ID); ok {
		account.Password = mergeSecret(account.Password, previous.Password)
		account.RefreshToken = mergeSecret(account.RefreshToken, previous.RefreshToken)
	}
	ctx, cancel := context.WithTimeout(request.Context(), 30*time.Second)
	defer cancel()
	if err := monitor.TestAccount(ctx, account); err != nil {
		writeError(writer, http.StatusBadRequest, err)
		return
	}
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true})
}

func (server *Server) testLLM(writer http.ResponseWriter, request *http.Request) {
	provider, ok := server.decodeProvider(writer, request)
	if !ok {
		return
	}
	ctx, cancel := context.WithTimeout(request.Context(), time.Duration(provider.Timeout)+10*time.Second)
	defer cancel()
	client := llm.New()
	if err := client.Test(ctx, provider); err != nil {
		server.setProviderStatus(provider.ID, "error", err.Error())
		writeError(writer, http.StatusBadRequest, err)
		return
	}
	server.setProviderStatus(provider.ID, "ok", "")
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true})
}

func (server *Server) installLLM(writer http.ResponseWriter, request *http.Request) {
	provider, ok := server.decodeProvider(writer, request)
	if !ok {
		return
	}
	ctx, cancel := context.WithTimeout(request.Context(), 30*time.Minute)
	defer cancel()
	if err := server.monitor.PrepareLocal(ctx, provider); err != nil {
		writeError(writer, http.StatusBadRequest, err)
		return
	}
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true})
}

func (server *Server) testClassifier(writer http.ResponseWriter, request *http.Request) {
	var input struct {
		Subject string `json:"subject"`
		Body    string `json:"body"`
		From    string `json:"from"`
	}
	if !decodeBody(writer, request, &input) {
		return
	}
	cfg := server.store.Get()
	writeJSON(writer, http.StatusOK, classifier.Evaluate(input.Subject, input.Body, input.From, cfg.Rules))
}

func (server *Server) markEventRead(writer http.ResponseWriter, request *http.Request) {
	id := request.PathValue("id")
	event, ok := server.bus.Find(id)
	if !ok {
		writeError(writer, http.StatusNotFound, errors.New("event not found"))
		return
	}
	if err := server.monitor.MarkEventRead(event); err != nil {
		writeError(writer, http.StatusBadRequest, err)
		return
	}
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true})
}

func (server *Server) testNotification(writer http.ResponseWriter, request *http.Request) {
	cfg := server.store.Get()
	code := "123456"
	copied := false
	if cfg.AutoCopyCode {
		ctx, cancel := notify.Deadline()
		err := notify.Copy(ctx, code)
		cancel()
		copied = err == nil
		if !copied {
			server.logger.Warn("copy notification test code failed", "error", err)
		}
	}
	if !cfg.DesktopNotifications {
		writeJSON(writer, http.StatusOK, map[string]any{"ok": true, "notification": false, "copied": copied})
		return
	}
	message := "验证码 " + code
	if copied {
		message += " 已复制"
	}
	if err := notify.Desktop("MailPulse notification test", message); err != nil {
		writeError(writer, http.StatusInternalServerError, err)
		return
	}
	writeJSON(writer, http.StatusOK, map[string]any{"ok": true, "notification": true, "copied": copied})
}

func (server *Server) startMicrosoftOAuth(writer http.ResponseWriter, request *http.Request) {
	ctx, cancel := context.WithTimeout(request.Context(), 20*time.Second)
	defer cancel()
	result, err := monitor.StartMicrosoftDeviceLogin(ctx)
	if err != nil {
		writeError(writer, http.StatusBadGateway, err)
		return
	}
	writeJSON(writer, http.StatusOK, result)
}

func (server *Server) exchangeMicrosoftOAuth(writer http.ResponseWriter, request *http.Request) {
	var input struct {
		DeviceCode string `json:"deviceCode"`
	}
	if !decodeBody(writer, request, &input) {
		return
	}
	if input.DeviceCode == "" {
		writeError(writer, http.StatusBadRequest, errors.New("deviceCode is required"))
		return
	}
	ctx, cancel := context.WithTimeout(request.Context(), 20*time.Second)
	defer cancel()
	result, err := monitor.CompleteMicrosoftDeviceLogin(ctx, input.DeviceCode)
	if err != nil {
		writeError(writer, http.StatusBadGateway, err)
		return
	}
	if result.Error != "" {
		writeError(writer, http.StatusBadRequest, errors.New(result.Error))
		return
	}
	writeJSON(writer, http.StatusOK, result)
}

func (server *Server) decodeProvider(writer http.ResponseWriter, request *http.Request) (config.LLMProvider, bool) {
	var provider config.LLMProvider
	if !decodeBody(writer, request, &provider) {
		return provider, false
	}
	if previous, ok := findLLM(server.store.Get(), provider.ID); ok {
		provider.APIKey = mergeSecret(provider.APIKey, previous.APIKey)
	}
	return provider, true
}

func (server *Server) setProviderStatus(id, status string, lastErr string) {
	if err := server.store.Mutate(func(cfg *config.AppConfig) {
		for index := range cfg.LLMProviders {
			if cfg.LLMProviders[index].ID == id {
				cfg.LLMProviders[index].Status = status
				cfg.LLMProviders[index].LastError = lastErr
				cfg.LLMProviders[index].LastTestedAt = time.Now().UTC()
			}
		}
	}); err != nil {
		server.logger.Error("save provider status failed", "error", err)
	}
}

func findAccount(cfg config.AppConfig, id string) (config.Account, bool) {
	for _, account := range cfg.Accounts {
		if account.ID == id {
			return account, true
		}
	}
	return config.Account{}, false
}

func findLLM(cfg config.AppConfig, id string) (config.LLMProvider, bool) {
	for _, provider := range cfg.LLMProviders {
		if provider.ID == id {
			return provider, true
		}
	}
	return config.LLMProvider{}, false
}

func secretValue(value string) string {
	if value == "" {
		return ""
	}
	return keepSecret
}

func mergeSecret(submitted, previous string) string {
	if submitted == keepSecret || submitted == "" {
		return previous
	}
	if submitted == clearSecret {
		return ""
	}
	return submitted
}

func normalizeSubmittedSecret(value string) string {
	if value == keepSecret || value == clearSecret {
		return ""
	}
	return value
}

func decodeBody(writer http.ResponseWriter, request *http.Request, output any) bool {
	decoder := json.NewDecoder(io.LimitReader(request.Body, 4<<20))
	if err := decoder.Decode(output); err != nil {
		writeError(writer, http.StatusBadRequest, fmt.Errorf("invalid JSON: %w", err))
		return false
	}
	return true
}

func writeJSON(writer http.ResponseWriter, status int, value any) {
	writer.Header().Set("Content-Type", "application/json")
	writer.WriteHeader(status)
	_ = json.NewEncoder(writer).Encode(value)
}

func writeError(writer http.ResponseWriter, status int, err error) {
	writeJSON(writer, status, map[string]any{"error": err.Error()})
}
