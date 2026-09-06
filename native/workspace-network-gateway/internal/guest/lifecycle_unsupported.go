//go:build !linux

package guest

import "errors"

type ownedPidFile struct{}

func claimPidFile(string) (*ownedPidFile, error) {
	return nil, errors.New("guest router process ownership requires Linux")
}

func (*ownedPidFile) release() {}

func stopOwnedGuest(string, string) error {
	return errors.New("guest router process ownership requires Linux")
}
