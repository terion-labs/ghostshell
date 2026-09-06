//go:build !linux

package guest

import (
	"errors"
	"net/netip"

	"github.com/terion-labs/ghostshell/native/workspace-network-gateway/internal/protocol"
)

func lockDownControlNetwork(netip.Addr) error {
	return errors.New("guest network setup requires Linux")
}

func configureTunnel(protocol.NetworkConfiguration) (packetDevice, error) {
	return nil, errors.New("guest TUN setup requires Linux")
}

func execInitProcess([]string) error {
	return errors.New("init wrapper requires Linux")
}
