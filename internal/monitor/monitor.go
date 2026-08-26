package monitor

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"mime"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	imap "github.com/emersion/go-imap/v2"
	"github.com/emersion/go-imap/v2/imapclient"
	"github.com/emersion/go-message/charset"
	"github.com/emersion/go-message/mail"
	"github.com/emersion/go-sasl"
	pop3 "github.com/knadh/go-pop3"

	"github.com/bighamx/MailPulse/internal/classifier"
	"github.com/bighamx/MailPulse/internal/config"
	"github.com/bighamx/MailPulse/internal/events"
	"github.com/bighamx/MailPulse/internal/llm"
	"github.com/bighamx/MailPulse/internal/notify"
)

type Monitor struct {
	store    *config.Store
	bus      *events.Bus
	llm      *llm.Client
	logger   *slog.Logger
	seen     map[string]struct{}
	seenFile string
	seenMu   sync.Mutex
	read     map[string]struct{}
	readUIDs map[string]imap.UID
	readMu   sync.Mutex
	runtimes map[string]context.CancelFunc
	mu       sync.Mutex
}

func New(store *config.Store, bus *events.Bus, logger *slog.Logger) *Monitor {
	monitor := &Monitor{
		store: store, bus: bus, llm: llm.New(), logger: logger,
		seen: map[string]struct{}{}, read: map[string]struct{}{}, readUIDs: map[string]imap.UID{}, runtimes: map[string]context.CancelFunc{},
		seenFile: filepath.Join(config.DefaultDir(), "seen.json"),
	}
	monitor.loadSeen()
	return monitor
}

func (monitor *Monitor) Start(parent context.Context) {
	monitor.mu.Lock()
	monitor.stopLocked()
	ctx, cancel := context.WithCancel(parent)
	monitor.runtimes["monitor"] = cancel
	monitor.mu.Unlock()

	cfg := monitor.store.Get()
	if cfg.LLMFallbackEnabled {
		if provider, ok := activeLLM(cfg); ok && provider.Mode == "local" && provider.Runtime == "ollama" && provider.AutoDownload {
			go monitor.ensureOllama(ctx, provider)
		}
	}
	for _, account := range cfg.Accounts {
		if !account.Enabled {
			continue
		}
		account := account
		go monitor.pollLoop(ctx, account)
	}
}

func (monitor *Monitor) Stop() {
	monitor.mu.Lock()
	monitor.stopLocked()
	monitor.mu.Unlock()
}

func (monitor *Monitor) stopLocked() {
	if cancel := monitor.runtimes["monitor"]; cancel != nil {
		cancel()
		delete(monitor.runtimes, "monitor")
	}
}

func (monitor *Monitor) Restart(ctx context.Context) {
	monitor.Stop()
	monitor.Start(ctx)
}

func (monitor *Monitor) pollLoop(ctx context.Context, account config.Account) {
	interval := time.Duration(account.PollInterval)
	if interval < 5*time.Second {
		interval = 45 * time.Second
	}
	for {
		if ctx.Err() != nil {
			return
		}
		monitor.setStatus(account.ID, "checking", "")
		if err := monitor.check(ctx, account); err != nil && !errors.Is(err, context.Canceled) {
			monitor.logger.Error("mail check failed", "account", account.Name, "error", err)
			monitor.setStatus(account.ID, "error", err.Error())
			monitor.bus.Publish(events.Event{
				Kind: events.KindSystem, Level: "error", AccountID: account.ID, Account: account.Name,
				Reason: err.Error(),
			})
		} else if ctx.Err() == nil {
			monitor.setStatus(account.ID, "ok", "")
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(interval):
		}
	}
}

func (monitor *Monitor) check(ctx context.Context, account config.Account) error {
	switch strings.ToLower(account.Protocol) {
	case "imap", "":
		return monitor.checkIMAP(ctx, account)
	case "pop3":
		return monitor.checkPOP3(ctx, account)
	default:
		return fmt.Errorf("unsupported protocol %q", account.Protocol)
	}
}

func (monitor *Monitor) checkPOP3(ctx context.Context, account config.Account) error {
	if account.Username == "" || account.Password == "" {
		return errors.New("POP3 username and password are required")
	}
	client := pop3.New(pop3.Opt{
		Host: account.Host, Port: account.Port, TLSEnabled: account.UseSSL, DialTimeout: 30 * time.Second,
	})
	connection, err := client.NewConn()
	if err != nil {
		return fmt.Errorf("connect POP3: %w", err)
	}
	defer connection.Quit()
	if err := connection.Auth(account.Username, account.Password); err != nil {
		return fmt.Errorf("POP3 login: %w", err)
	}
	identifiers, err := connection.Uidl(0)
	if err != nil {
		return fmt.Errorf("POP3 UIDL: %w", err)
	}
	if len(identifiers) > 20 {
		identifiers = identifiers[len(identifiers)-20:]
	}
	cfg := monitor.store.Get()
	for _, identifier := range identifiers {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		key := account.ID + "|pop3-" + identifier.UID
		if monitor.seenOnce(key) {
			continue
		}
		raw, err := connection.RetrRaw(identifier.ID)
		if err != nil {
			monitor.logger.Warn("POP3 fetch failed", "uid", identifier.UID, "error", err)
			continue
		}
		parsed, err := parseMessage(raw.Bytes())
		if err != nil {
			monitor.logger.Warn("parse POP3 message failed", "uid", identifier.UID, "error", err)
			continue
		}
		result := classifier.Evaluate(parsed.Subject, parsed.Text, parsed.From, cfg.Rules)
		source := "rules"
		if !result.Matched && cfg.LLMFallbackEnabled {
			if provider, ok := activeLLM(cfg); ok {
				if llmResult, err := monitor.llm.Classify(ctx, provider, cfg.LLMPrompt, parsed.Subject, parsed.Text); err == nil {
					source = "llm"
					result = classifier.Result{Matched: llmResult.Matched, Code: llmResult.Code, URL: llmResult.URL, Reason: llmResult.Reason}
					monitor.setProviderStatus(provider.ID, "ok", "")
				} else {
					monitor.setProviderStatus(provider.ID, "error", err.Error())
				}
			}
		}
		if result.Matched {
			monitor.bus.Publish(events.Event{
				Kind: events.KindMatch, AccountID: account.ID, Account: account.Name, MessageID: "pop3-" + identifier.UID,
				From: parsed.From, Subject: parsed.Subject, Code: result.Code, URL: result.URL,
				Reason: result.Reason + " (" + source + ")",
			})
			monitor.notifyAsync(cfg, events.Event{
				Kind: events.KindMatch, Account: account.Name, Subject: parsed.Subject,
				Code: result.Code, URL: result.URL, Reason: result.Reason,
			})
		}
	}
	monitor.store.Mutate(func(update *config.AppConfig) {
		for index := range update.Accounts {
			if update.Accounts[index].ID == account.ID {
				update.Accounts[index].LastCheckedAt = time.Now().UTC()
				update.Accounts[index].LastMessageAt = time.Now().UTC()
			}
		}
	})
	return nil
}

func (monitor *Monitor) checkIMAP(ctx context.Context, account config.Account) error {
	client, err := connectIMAP(ctx, account)
	if err != nil {
		return err
	}
	defer client.Close()
	if _, err := client.Select("INBOX", nil).Wait(); err != nil {
		return fmt.Errorf("select INBOX: %w", err)
	}
	search, err := client.UIDSearch(&imap.SearchCriteria{NotFlag: []imap.Flag{imap.FlagSeen}}, nil).Wait()
	if err != nil {
		return fmt.Errorf("search unseen: %w", err)
	}
	uids := search.AllUIDs()
	if len(uids) > 20 {
		uids = uids[len(uids)-20:]
	}
	if len(uids) == 0 {
		monitor.store.Mutate(func(cfg *config.AppConfig) {
			for index := range cfg.Accounts {
				if cfg.Accounts[index].ID == account.ID {
					cfg.Accounts[index].LastCheckedAt = time.Now().UTC()
				}
			}
		})
		return nil
	}
	set := imap.UIDSetNum(uids...)
	section := &imap.FetchItemBodySection{Peek: true}
	messages, err := client.Fetch(set, &imap.FetchOptions{UID: true, Envelope: true, BodySection: []*imap.FetchItemBodySection{section}}).Collect()
	if err != nil {
		return fmt.Errorf("fetch messages: %w", err)
	}
	cfg := monitor.store.Get()
	for _, message := range messages {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		raw := message.FindBodySection(section)
		parsed, err := parseMessage(raw)
		if err != nil {
			monitor.logger.Warn("parse message failed", "uid", message.UID, "error", err)
			parsed = ParsedMessage{From: addressString(message.Envelope.From), Subject: message.Envelope.Subject}
		}
		messageID := message.Envelope.MessageID
		if messageID == "" {
			messageID = fmt.Sprintf("uid-%d-%d", message.UID, message.InternalDate.Unix())
		}
		key := account.ID + "|" + messageID
		if monitor.seenOnce(key) {
			continue
		}
		result := classifier.Evaluate(parsed.Subject, parsed.Text, parsed.From, cfg.Rules)
		source := "rules"
		if !result.Matched && cfg.LLMFallbackEnabled {
			if provider, ok := activeLLM(cfg); ok {
				llmResult, err := monitor.llm.Classify(ctx, provider, cfg.LLMPrompt, parsed.Subject, parsed.Text)
				if err != nil {
					monitor.logger.Warn("llm fallback failed", "provider", provider.Name, "error", err)
					monitor.setProviderStatus(provider.ID, "error", err.Error())
				} else {
					source = "llm"
					result = classifier.Result{Matched: llmResult.Matched, Code: llmResult.Code, URL: llmResult.URL, Reason: llmResult.Reason}
					monitor.setProviderStatus(provider.ID, "ok", "")
				}
			}
		}
		if result.Matched {
			canMark := account.MarkMatchedAsRead && !containsFlag(message.Flags, imap.FlagSeen)
			event := events.Event{
				Kind: events.KindMatch, AccountID: account.ID, Account: account.Name, MessageID: messageID,
				From: parsed.From, Subject: parsed.Subject, Code: result.Code, URL: result.URL,
				Reason: result.Reason + " (" + source + ")", MarkAsRead: canMark,
			}
			monitor.bus.Publish(event)
			monitor.notifyAsync(cfg, event)
			if event.MarkAsRead {
				monitor.readMu.Lock()
				monitor.read[key] = struct{}{}
				monitor.readUIDs[key] = message.UID
				monitor.readMu.Unlock()
			}
		}
		monitor.store.Mutate(func(update *config.AppConfig) {
			for index := range update.Accounts {
				if update.Accounts[index].ID == account.ID {
					update.Accounts[index].LastMessageAt = time.Now().UTC()
					update.Accounts[index].LastCheckedAt = time.Now().UTC()
				}
			}
		})
	}
	if len(monitor.read) > 0 {
		monitor.markRead(client, account.ID)
	}
	return nil
}

func (monitor *Monitor) notifyAsync(cfg config.AppConfig, event events.Event) {
	if !cfg.DesktopNotifications && !(cfg.AutoCopyCode && event.Code != "") {
		return
	}
	go func() {
		copied := false
		if cfg.AutoCopyCode && event.Code != "" {
			ctx, cancel := notify.Deadline()
			err := notify.Copy(ctx, event.Code)
			cancel()
			if err != nil {
				monitor.logger.Warn("copy verification code failed", "error", err)
			} else {
				copied = true
			}
		}
		if !cfg.DesktopNotifications {
			return
		}
		var message strings.Builder
		if event.Code != "" {
			if copied {
				message.WriteString("验证码 " + event.Code + " 已复制\n")
			} else {
				message.WriteString("验证码：" + event.Code + "\n")
			}
		}
		if event.URL != "" {
			message.WriteString("确认链接：" + event.URL + "\n")
		}
		if event.Subject != "" {
			message.WriteString(event.Subject + "\n")
		}
		if event.Reason != "" {
			message.WriteString(event.Reason)
		}
		if err := notify.Desktop("MailPulse · "+event.Account, strings.TrimSpace(message.String())); err != nil {
			monitor.logger.Warn("desktop notification failed", "error", err)
		}
	}()
}

func (monitor *Monitor) markRead(client *imapclient.Client, accountID string) {
	var set imap.UIDSet
	monitor.readMu.Lock()
	pending := make([]string, 0, len(monitor.read))
	for key, uid := range monitor.readUIDs {
		if _, wanted := monitor.read[key]; wanted && strings.HasPrefix(key, accountID+"|") {
			set.AddNum(uid)
			pending = append(pending, key)
		}
	}
	monitor.readMu.Unlock()
	if len(set) == 0 {
		return
	}
	flags := imap.StoreFlags{Op: imap.StoreFlagsAdd, Flags: []imap.Flag{imap.FlagSeen}, Silent: true}
	if err := client.Store(set, &flags, nil).Close(); err != nil {
		monitor.logger.Warn("mark seen failed", "error", err)
		return
	}
	monitor.readMu.Lock()
	for _, key := range pending {
		delete(monitor.read, key)
		delete(monitor.readUIDs, key)
	}
	monitor.readMu.Unlock()
}

func (monitor *Monitor) MarkEventRead(event events.Event) error {
	if event.AccountID == "" || event.MessageID == "" {
		return errors.New("event cannot be marked read")
	}
	monitor.readMu.Lock()
	monitor.read[event.AccountID+"|"+event.MessageID] = struct{}{}
	monitor.readMu.Unlock()
	return nil
}

func (monitor *Monitor) seenOnce(key string) bool {
	monitor.seenMu.Lock()
	defer monitor.seenMu.Unlock()
	if _, exists := monitor.seen[key]; exists {
		return true
	}
	monitor.seen[key] = struct{}{}
	if err := monitor.saveSeenLocked(); err != nil {
		monitor.logger.Warn("save seen state failed", "error", err)
	}
	return false
}

func (monitor *Monitor) loadSeen() {
	data, err := os.ReadFile(monitor.seenFile)
	if errors.Is(err, os.ErrNotExist) {
		return
	}
	if err != nil {
		monitor.logger.Warn("read seen state failed", "error", err)
		return
	}
	var keys []string
	if err := json.Unmarshal(data, &keys); err != nil {
		monitor.logger.Warn("parse seen state failed", "error", err)
		return
	}
	monitor.seenMu.Lock()
	defer monitor.seenMu.Unlock()
	for _, key := range keys {
		monitor.seen[key] = struct{}{}
	}
}

func (monitor *Monitor) saveSeenLocked() error {
	keys := make([]string, 0, len(monitor.seen))
	for key := range monitor.seen {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	data, err := json.MarshalIndent(keys, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(monitor.seenFile), 0o700); err != nil {
		return err
	}
	temp := monitor.seenFile + ".tmp"
	if err := os.WriteFile(temp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(temp, monitor.seenFile)
}

func (monitor *Monitor) setStatus(accountID, status, lastError string) {
	if err := monitor.store.Mutate(func(cfg *config.AppConfig) {
		for index := range cfg.Accounts {
			if cfg.Accounts[index].ID == accountID {
				cfg.Accounts[index].Status = status
				cfg.Accounts[index].LastError = lastError
				if status == "ok" {
					cfg.Accounts[index].LastCheckedAt = time.Now().UTC()
				}
			}
		}
	}); err != nil {
		monitor.logger.Error("save account status failed", "error", err)
	}
}

func (monitor *Monitor) setProviderStatus(id, status, lastError string) {
	if err := monitor.store.Mutate(func(cfg *config.AppConfig) {
		for index := range cfg.LLMProviders {
			if cfg.LLMProviders[index].ID == id {
				cfg.LLMProviders[index].Status = status
				cfg.LLMProviders[index].LastError = lastError
				cfg.LLMProviders[index].LastTestedAt = time.Now().UTC()
			}
		}
	}); err != nil {
		monitor.logger.Error("save provider status failed", "error", err)
	}
}

func (monitor *Monitor) ensureOllama(ctx context.Context, provider config.LLMProvider) {
	if !llm.OllamaInstalled() {
		monitor.setProviderStatus(provider.ID, "missing-runtime", "Ollama is not installed")
		return
	}
	monitor.setProviderStatus(provider.ID, "checking", "")
	deadline, cancel := context.WithTimeout(ctx, 30*time.Second)
	has, err := llm.OllamaHasModel(deadline, provider.BaseURL, provider.Model)
	cancel()
	if err == nil && has {
		monitor.setProviderStatus(provider.ID, "ready", "")
		return
	}
	monitor.setProviderStatus(provider.ID, "downloading", "")
	pullCtx, cancel := context.WithTimeout(ctx, 30*time.Minute)
	defer cancel()
	if err := llm.PullOllamaModel(pullCtx, provider.Model); err != nil {
		monitor.setProviderStatus(provider.ID, "error", err.Error())
		return
	}
	monitor.setProviderStatus(provider.ID, "ready", "")
}

func (monitor *Monitor) PrepareLocal(ctx context.Context, provider config.LLMProvider) error {
	if provider.Runtime != "ollama" {
		return errors.New("only the built-in Ollama local runtime is managed automatically")
	}
	if !llm.OllamaInstalled() {
		return errors.New("Ollama is not installed; install it from https://ollama.com/download")
	}
	has, err := llm.OllamaHasModel(ctx, provider.BaseURL, provider.Model)
	if err != nil {
		return err
	}
	if has {
		monitor.setProviderStatus(provider.ID, "ready", "")
		return nil
	}
	monitor.setProviderStatus(provider.ID, "downloading", "")
	if err := llm.PullOllamaModel(ctx, provider.Model); err != nil {
		monitor.setProviderStatus(provider.ID, "error", err.Error())
		return err
	}
	monitor.setProviderStatus(provider.ID, "ready", "")
	return nil
}

func connectIMAP(ctx context.Context, account config.Account) (*imapclient.Client, error) {
	address := fmt.Sprintf("%s:%d", account.Host, account.Port)
	options := &imapclient.Options{WordDecoder: &mime.WordDecoder{CharsetReader: charset.Reader}}
	var client *imapclient.Client
	var err error
	if account.UseSSL {
		client, err = imapclient.DialTLS(address, options)
	} else {
		client, err = imapclient.DialInsecure(address, options)
	}
	if err != nil {
		return nil, fmt.Errorf("connect IMAP: %w", err)
	}
	if account.UseOAuth {
		if account.RefreshToken == "" {
			client.Close()
			return nil, errors.New("OAuth refresh token is required")
		}
		token, err := RefreshMicrosoftToken(ctx, account.RefreshToken)
		if err != nil {
			client.Close()
			return nil, fmt.Errorf("refresh Microsoft OAuth token: %w", err)
		}
		user := account.OAuthUserEmail
		if user == "" {
			user = account.Username
		}
		if err := client.Authenticate(sasl.NewOAuthBearerClient(&sasl.OAuthBearerOptions{
			Username: user, Token: token,
		})); err != nil {
			client.Close()
			return nil, fmt.Errorf("IMAP OAuth: %w", err)
		}
		return client, nil
	}
	if account.Username == "" || account.Password == "" {
		client.Close()
		return nil, errors.New("IMAP username and password are required")
	}
	if err := client.Login(account.Username, account.Password).Wait(); err != nil {
		client.Close()
		return nil, fmt.Errorf("IMAP login: %w", err)
	}
	return client, nil
}

func TestAccount(ctx context.Context, account config.Account) error {
	if strings.EqualFold(account.Protocol, "pop3") {
		client := pop3.New(pop3.Opt{Host: account.Host, Port: account.Port, TLSEnabled: account.UseSSL, DialTimeout: 30 * time.Second})
		connection, err := client.NewConn()
		if err != nil {
			return err
		}
		defer connection.Quit()
		if err := connection.Auth(account.Username, account.Password); err != nil {
			return err
		}
		_, _, err = connection.Stat()
		return err
	}
	client, err := connectIMAP(ctx, account)
	if err != nil {
		return err
	}
	defer client.Close()
	_, err = client.Select("INBOX", nil).Wait()
	return err
}

type ParsedMessage struct {
	From    string
	Subject string
	Text    string
}

func parseMessage(raw []byte) (ParsedMessage, error) {
	reader, err := mail.CreateReader(strings.NewReader(string(raw)))
	if err != nil {
		return ParsedMessage{}, err
	}
	defer reader.Close()
	var text strings.Builder
	for {
		part, err := reader.NextPart()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return ParsedMessage{}, err
		}
		mediaType, _, mediaErr := mime.ParseMediaType(part.Header.Get("Content-Type"))
		if mediaErr == nil && (mediaType == "text/plain" || mediaType == "text/html") {
			data, err := io.ReadAll(io.LimitReader(part.Body, 2<<20))
			if err == nil {
				text.Write(data)
				text.WriteByte('\n')
			}
		}
	}
	from := ""
	if addresses, err := reader.Header.AddressList("From"); err == nil && len(addresses) > 0 {
		from = addresses[0].String()
	}
	return ParsedMessage{From: from, Subject: reader.Header.Get("Subject"), Text: text.String()}, nil
}

func addressString(addresses []imap.Address) string {
	if len(addresses) == 0 {
		return ""
	}
	return addresses[0].Addr()
}

func containsFlag(flags []imap.Flag, wanted imap.Flag) bool {
	for _, flag := range flags {
		if flag == wanted {
			return true
		}
	}
	return false
}

func activeLLM(cfg config.AppConfig) (config.LLMProvider, bool) {
	for _, provider := range cfg.LLMProviders {
		if provider.ID == cfg.ActiveLLMID {
			return provider, true
		}
	}
	return config.LLMProvider{}, false
}
