package host

import (
	"io"
	"os"
	"sync"

	"golang.zx2c4.com/wireguard/tun"
)

// memoryTUN joins an embedded VPN to the guest channel or host socket stack.
// It creates no OS interface and exposes no unauthenticated UDP relay.
type memoryTUN struct {
	mtu       int
	towardVPN chan []byte
	fromVPN   chan []byte
	done      chan struct{}
	events    chan tun.Event
	once      sync.Once
}

func newMemoryTUN(mtu int) *memoryTUN {
	t := &memoryTUN{mtu: mtu, towardVPN: make(chan []byte, 256), fromVPN: make(chan []byte, 256), done: make(chan struct{}), events: make(chan tun.Event, 1)}
	t.events <- tun.EventUp
	return t
}
func (t *memoryTUN) File() *os.File           { return nil }
func (t *memoryTUN) MTU() (int, error)        { return t.mtu, nil }
func (t *memoryTUN) Name() (string, error)    { return "ghostshell-memory", nil }
func (t *memoryTUN) Events() <-chan tun.Event { return t.events }
func (t *memoryTUN) BatchSize() int           { return 1 }
func (t *memoryTUN) Close() error {
	t.once.Do(func() { close(t.done); close(t.events) })
	return nil
}
func (t *memoryTUN) Read(buffers [][]byte, sizes []int, offset int) (int, error) {
	select {
	case <-t.done:
		return 0, os.ErrClosed
	default:
	}
	select {
	case <-t.done:
		return 0, os.ErrClosed
	case packet := <-t.towardVPN:
		if len(buffers) == 0 || len(sizes) == 0 || offset < 0 || len(buffers[0])-offset < len(packet) {
			return 0, io.ErrShortBuffer
		}
		sizes[0] = copy(buffers[0][offset:], packet)
		return 1, nil
	}
}
func (t *memoryTUN) Write(buffers [][]byte, offset int) (int, error) {
	select {
	case <-t.done:
		return 0, os.ErrClosed
	default:
	}
	for i, buffer := range buffers {
		if offset < 0 || offset > len(buffer) || len(buffer)-offset > t.mtu {
			return i, io.ErrShortBuffer
		}
		packet := append([]byte(nil), buffer[offset:]...)
		select {
		case <-t.done:
			return i, os.ErrClosed
		case t.fromVPN <- packet:
		}
	}
	return len(buffers), nil
}

type memoryPacketTransport struct{ tun *memoryTUN }

func (p memoryPacketTransport) Close() error { return p.tun.Close() }
func (p memoryPacketTransport) Read(buffer []byte) (int, error) {
	select {
	case <-p.tun.done:
		return 0, os.ErrClosed
	default:
	}
	select {
	case <-p.tun.done:
		return 0, os.ErrClosed
	case packet := <-p.tun.fromVPN:
		if len(buffer) < len(packet) {
			return 0, io.ErrShortBuffer
		}
		return copy(buffer, packet), nil
	}
}
func (p memoryPacketTransport) Write(packet []byte) (int, error) {
	select {
	case <-p.tun.done:
		return 0, os.ErrClosed
	default:
	}
	if len(packet) > p.tun.mtu {
		return 0, io.ErrShortBuffer
	}
	owned := append([]byte(nil), packet...)
	select {
	case <-p.tun.done:
		return 0, os.ErrClosed
	case p.tun.towardVPN <- owned:
		return len(packet), nil
	}
}
