package guest

import (
	"bytes"
	"testing"

	"github.com/terion-labs/asura/native/workspace-network-gateway/internal/protocol"
)

func TestReadAuthenticationKeyRequiresExactInput(t *testing.T) {
	t.Parallel()
	valid := bytes.Repeat([]byte{7}, protocol.AuthenticationKeyLength)
	tests := map[string][]byte{
		"short": append([]byte{}, valid[:len(valid)-1]...),
		"long":  append(append([]byte{}, valid...), 9),
	}
	for name, input := range tests {
		name, input := name, input
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			if _, err := readAuthenticationKey(bytes.NewReader(input)); err == nil {
				t.Fatal("expected invalid authentication key input to fail")
			}
		})
	}

	key, err := readAuthenticationKey(bytes.NewReader(valid))
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(key, valid) {
		t.Fatal("authentication key was not preserved")
	}
}
