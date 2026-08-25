package server

import "testing"

func TestMergeSecretKeepsAndClears(t *testing.T) {
	if got := mergeSecret("", "old"); got != "old" {
		t.Fatalf("blank changed secret: %q", got)
	}
	if got := mergeSecret(keepSecret, "old"); got != "old" {
		t.Fatalf("keep sentinel changed secret: %q", got)
	}
	if got := mergeSecret(clearSecret, "old"); got != "" {
		t.Fatalf("clear sentinel changed secret: %q", got)
	}
	if got := mergeSecret("new", "old"); got != "new" {
		t.Fatalf("new secret was not used: %q", got)
	}
}
