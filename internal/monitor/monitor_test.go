package monitor

import "testing"

func TestParseMessageWithCharset(t *testing.T) {
	raw := []byte("From: Notice <noreply@example.com>\r\n" +
		"Subject: Your verification code\r\n" +
		"Content-Type: text/plain; charset=utf-8\r\n\r\n" +
		"Your code is 123456.\r\n")
	got, err := parseMessage(raw)
	if err != nil {
		t.Fatal(err)
	}
	if got.Subject != "Your verification code" || got.From == "" {
		t.Fatalf("unexpected metadata: %#v", got)
	}
	if !contains(got.Text, "123456") {
		t.Fatalf("body was not parsed: %q", got.Text)
	}
}

func contains(value, needle string) bool {
	for index := 0; index+len(needle) <= len(value); index++ {
		if value[index:index+len(needle)] == needle {
			return true
		}
	}
	return false
}
