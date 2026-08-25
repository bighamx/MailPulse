package events

import (
	"sync"
	"time"

	"github.com/google/uuid"
)

type Kind string

const (
	KindMatch  Kind = "match"
	KindLLM    Kind = "llm"
	KindSystem Kind = "system"
)

type Event struct {
	ID         string    `json:"id"`
	Kind       Kind      `json:"kind"`
	Time       time.Time `json:"time"`
	AccountID  string    `json:"accountId,omitempty"`
	Account    string    `json:"account,omitempty"`
	MessageID  string    `json:"messageId,omitempty"`
	From       string    `json:"from,omitempty"`
	Subject    string    `json:"subject,omitempty"`
	Code       string    `json:"code,omitempty"`
	URL        string    `json:"url,omitempty"`
	Reason     string    `json:"reason,omitempty"`
	MarkAsRead bool      `json:"markAsRead"`
	Level      string    `json:"level,omitempty"`
}

type Bus struct {
	mu        sync.RWMutex
	limit     int
	events    []Event
	listeners map[string]chan Event
}

func NewBus(limit int) *Bus {
	if limit <= 0 {
		limit = 500
	}
	return &Bus{limit: limit, listeners: map[string]chan Event{}}
}

func (bus *Bus) Publish(event Event) []func() {
	if event.ID == "" {
		event.ID = uuid.NewString()
	}
	if event.Time.IsZero() {
		event.Time = time.Now().UTC()
	}
	bus.mu.Lock()
	bus.events = append(bus.events, event)
	if len(bus.events) > bus.limit {
		bus.events = bus.events[len(bus.events)-bus.limit:]
	}
	listeners := make([]chan Event, 0, len(bus.listeners))
	for _, listener := range bus.listeners {
		listeners = append(listeners, listener)
	}
	bus.mu.Unlock()

	var callbacks []func()
	for _, listener := range listeners {
		listener := listener
		callbacks = append(callbacks, func() {
			select {
			case listener <- event:
			default:
			}
		})
	}
	for _, publish := range callbacks {
		publish()
	}
	return nil
}

func (bus *Bus) Recent(limit int) []Event {
	bus.mu.RLock()
	defer bus.mu.RUnlock()
	if limit <= 0 || limit > len(bus.events) {
		limit = len(bus.events)
	}
	out := make([]Event, limit)
	copy(out, bus.events[len(bus.events)-limit:])
	for index, value := range out {
		value.MarkAsRead = false
		out[index] = value
	}
	return out
}

func (bus *Bus) Clear() {
	bus.mu.Lock()
	bus.events = nil
	bus.mu.Unlock()
}

func (bus *Bus) Subscribe() (string, <-chan Event, func()) {
	id := uuid.NewString()
	channel := make(chan Event, 64)
	bus.mu.Lock()
	bus.listeners[id] = channel
	bus.mu.Unlock()
	return id, channel, func() {
		bus.mu.Lock()
		delete(bus.listeners, id)
		bus.mu.Unlock()
	}
}

func (bus *Bus) Find(id string) (Event, bool) {
	bus.mu.RLock()
	defer bus.mu.RUnlock()
	for index := len(bus.events) - 1; index >= 0; index-- {
		if bus.events[index].ID == id {
			return bus.events[index], true
		}
	}
	return Event{}, false
}
