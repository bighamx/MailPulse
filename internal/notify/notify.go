package notify

import (
	"context"
	"errors"
	"fmt"
	"os/exec"
	"runtime"
	"strings"
	"time"

	"github.com/gen2brain/beeep"
)

// A tiny embedded PNG keeps the single-file service self-contained.
var icon = []byte{
	0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
	0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
	0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00,
	0x0d, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00,
	0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00, 0x00, 0x00, 0x00, 0x49,
	0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
}

func Desktop(title, message string) error {
	beeep.AppName = "MailPulse"
	return beeep.Notify(title, message, icon)
}

func Copy(ctx context.Context, text string) error {
	if strings.TrimSpace(text) == "" {
		return errors.New("clipboard text is empty")
	}
	var command *exec.Cmd
	switch runtime.GOOS {
	case "darwin":
		command = exec.CommandContext(ctx, "pbcopy")
	case "windows":
		command = exec.CommandContext(ctx, "powershell", "-NoProfile", "-Command", "Set-Clipboard", "-Value", text)
	case "linux":
		if _, err := exec.LookPath("wl-copy"); err == nil {
			command = exec.CommandContext(ctx, "wl-copy")
		} else {
			command = exec.CommandContext(ctx, "xclip", "-selection", "clipboard")
		}
	default:
		return fmt.Errorf("unsupported platform: %s", runtime.GOOS)
	}
	input, err := command.StdinPipe()
	if err != nil {
		return err
	}
	if err := command.Start(); err != nil {
		return err
	}
	if _, err := input.Write([]byte(text)); err != nil {
		_ = input.Close()
		_ = command.Wait()
		return err
	}
	if err := input.Close(); err != nil {
		return err
	}
	return command.Wait()
}

func Deadline() (context.Context, context.CancelFunc) {
	return context.WithTimeout(context.Background(), 5*time.Second)
}
