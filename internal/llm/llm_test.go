package llm

import "testing"

func TestParseClassification(t *testing.T) {
	tests := []struct {
		name string
		in   string
		code string
	}{
		{"json", `{"matched":true,"code":"123456","url":"","reason":"code"}`, "123456"},
		{"fence", "```json\n{\"matched\":true,\"code\":\"654321\"}\n```", "654321"},
		{"thinking", "<think>reasoning</think>{\"matched\":true,\"code\":\"112233\"}", "112233"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := parseClassification(test.in)
			if err != nil {
				t.Fatal(err)
			}
			if !got.Matched || got.Code != test.code {
				t.Fatalf("got %#v", got)
			}
		})
	}
}
