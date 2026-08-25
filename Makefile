BINARY := mailpulsed
VERSION ?= 2.0.0

.PHONY: build test fmt lint package clean

build:
	go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o bin/$(BINARY) ./cmd/mailpulsed

test:
	go test ./...

fmt:
	gofmt -w cmd internal web

lint:
	go vet ./...

package: test
	mkdir -p dist
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o dist/$(BINARY)-linux-amd64 ./cmd/mailpulsed
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o dist/$(BINARY)-linux-arm64 ./cmd/mailpulsed
	CGO_ENABLED=0 GOOS=darwin GOARCH=amd64 go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o dist/$(BINARY)-darwin-amd64 ./cmd/mailpulsed
	CGO_ENABLED=0 GOOS=darwin GOARCH=arm64 go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o dist/$(BINARY)-darwin-arm64 ./cmd/mailpulsed
	CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -ldflags "-s -w -X main.version=$(VERSION)" -o dist/$(BINARY)-windows-amd64.exe ./cmd/mailpulsed

clean:
	go clean
	rm -rf bin dist
