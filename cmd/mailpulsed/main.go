package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/bighamx/MailPulse/internal/config"
	"github.com/bighamx/MailPulse/internal/events"
	"github.com/bighamx/MailPulse/internal/monitor"
	"github.com/bighamx/MailPulse/internal/server"
)

var version = "2.0.0"

func main() {
	var (
		address   = flag.String("address", "127.0.0.1:8787", "HTTP/WebUI listen address")
		configArg = flag.String("config", "", "configuration file path")
		dataDir   = flag.String("data", "", "data directory (config, logs, state)")
		logFile   = flag.String("log", "", "log file path (default stderr)")
		showVer   = flag.Bool("version", false, "print version")
	)
	flag.Parse()
	if *showVer {
		fmt.Println("mailpulsed", version)
		return
	}
	if *dataDir != "" {
		if err := os.Setenv("MAILPULSE_DATA_DIR", *dataDir); err != nil {
			fatal(err)
		}
	}

	logger := newLogger(*logFile)
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	store, err := config.Open(*configArg)
	if err != nil {
		logger.Error("open config failed", "error", err)
		os.Exit(1)
	}
	cfg := store.Get()
	bus := events.NewBus(cfg.EventRetention)
	mailMonitor := monitor.New(store, bus, logger)
	mailMonitor.Start(ctx)

	httpServer := server.New(ctx, *address, store, bus, mailMonitor, logger)
	errCh := make(chan error, 1)
	go func() { errCh <- httpServer.ListenAndServe() }()
	logger.Info("MailPulse service started", "version", version, "config", configPath(*configArg), "dataDir", config.DefaultDir())

	select {
	case <-ctx.Done():
		logger.Info("shutdown signal received")
	case err := <-errCh:
		if err != nil {
			logger.Error("HTTP server failed", "error", err)
		}
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	mailMonitor.Stop()
	_ = httpServer.Shutdown(shutdownCtx)
}

func configPath(argument string) string {
	if argument != "" {
		absolute, err := filepath.Abs(argument)
		if err == nil {
			return absolute
		}
		return argument
	}
	return config.DefaultPath()
}

func newLogger(path string) *slog.Logger {
	if path == "" {
		return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		fatal(err)
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		fatal(err)
	}
	var output io.Writer = file
	return slog.New(slog.NewJSONHandler(output, &slog.HandlerOptions{Level: slog.LevelInfo}))
}

func fatal(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}
