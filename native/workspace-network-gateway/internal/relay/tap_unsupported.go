//go:build !linux

package relay

import (
	"context"
	"errors"
)

func RunTap(context.Context) error { return errors.New("relay TAP requires Linux") }
