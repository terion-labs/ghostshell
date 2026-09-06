//go:build linux

package guest

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"syscall"
	"time"
)

const (
	guestStopGracePeriod = 5 * time.Second
	guestStopPollPeriod  = 25 * time.Millisecond
)

type ownedPidFile struct {
	path string
	pid  int
}

func claimPidFile(path string) (*ownedPidFile, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, fmt.Errorf("create guest PID directory: %w", err)
	}
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		if errors.Is(err, os.ErrExist) {
			return nil, errors.New("another workspace guest router owns the PID file")
		}
		return nil, fmt.Errorf("claim guest PID file: %w", err)
	}
	pid := os.Getpid()
	if _, err := fmt.Fprintf(file, "%d\n", pid); err != nil {
		_ = file.Close()
		_ = os.Remove(path)
		return nil, fmt.Errorf("write guest PID file: %w", err)
	}
	if err := file.Close(); err != nil {
		_ = os.Remove(path)
		return nil, fmt.Errorf("close guest PID file: %w", err)
	}
	return &ownedPidFile{path: path, pid: pid}, nil
}

func (file *ownedPidFile) release() {
	pid, err := readPidFile(file.path)
	if err == nil && pid == file.pid {
		_ = os.Remove(file.path)
	}
}

func stopOwnedGuest(pidFilePath string, socketPath string) error {
	pid, err := readPidFile(pidFilePath)
	if errors.Is(err, os.ErrNotExist) {
		return removeStaleSocket(socketPath)
	}
	if err != nil {
		return err
	}
	if pid <= 1 {
		return errors.New("guest PID file contains an unsafe process identifier")
	}

	cmdline, err := os.ReadFile(filepath.Join("/proc", strconv.Itoa(pid), "cmdline"))
	if errors.Is(err, os.ErrNotExist) {
		return removeStaleGuestFiles(pidFilePath, socketPath, pid)
	}
	if err != nil {
		return fmt.Errorf("inspect workspace guest router process: %w", err)
	}
	if !ownsGuestProcess(cmdline, pidFilePath, socketPath) {
		return errors.New("guest PID file points to a process that is not the owned workspace router")
	}

	if err := syscall.Kill(pid, syscall.SIGTERM); err != nil && !errors.Is(err, syscall.ESRCH) {
		return fmt.Errorf("stop workspace guest router: %w", err)
	}
	if !waitForProcessExit(pid, guestStopGracePeriod) {
		if err := syscall.Kill(pid, syscall.SIGKILL); err != nil && !errors.Is(err, syscall.ESRCH) {
			return fmt.Errorf("kill unresponsive workspace guest router: %w", err)
		}
		if !waitForProcessExit(pid, time.Second) {
			return errors.New("workspace guest router did not stop")
		}
	}
	return removeStaleGuestFiles(pidFilePath, socketPath, pid)
}

func ownsGuestProcess(cmdline []byte, pidFilePath string, socketPath string) bool {
	arguments := bytes.Split(bytes.TrimSuffix(cmdline, []byte{0}), []byte{0})
	return hasArgumentValue(arguments, "--pid-file", pidFilePath) &&
		hasArgumentValue(arguments, "--socket", socketPath) &&
		hasArgument(arguments, "guest")
}

func hasArgument(arguments [][]byte, expected string) bool {
	for _, argument := range arguments {
		if string(argument) == expected {
			return true
		}
	}
	return false
}

func hasArgumentValue(arguments [][]byte, name string, expected string) bool {
	for index := 0; index+1 < len(arguments); index++ {
		if string(arguments[index]) == name && string(arguments[index+1]) == expected {
			return true
		}
	}
	return false
}

func readPidFile(path string) (int, error) {
	information, err := os.Lstat(path)
	if err != nil {
		return 0, err
	}
	if !information.Mode().IsRegular() {
		return 0, errors.New("guest PID path is not a regular file")
	}
	contents, err := os.ReadFile(path)
	if err != nil {
		return 0, fmt.Errorf("read guest PID file: %w", err)
	}
	pid, err := strconv.Atoi(string(bytes.TrimSpace(contents)))
	if err != nil {
		return 0, errors.New("guest PID file is invalid")
	}
	return pid, nil
}

func waitForProcessExit(pid int, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if err := syscall.Kill(pid, 0); errors.Is(err, syscall.ESRCH) {
			return true
		}
		time.Sleep(guestStopPollPeriod)
	}
	return errors.Is(syscall.Kill(pid, 0), syscall.ESRCH)
}

func removeStaleGuestFiles(pidFilePath string, socketPath string, expectedPid int) error {
	pid, err := readPidFile(pidFilePath)
	if err == nil && pid == expectedPid {
		if err := os.Remove(pidFilePath); err != nil && !errors.Is(err, os.ErrNotExist) {
			return fmt.Errorf("remove stale guest PID file: %w", err)
		}
	} else if err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return removeStaleSocket(socketPath)
}

func removeStaleSocket(path string) error {
	information, err := os.Lstat(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("inspect stale guest socket: %w", err)
	}
	if information.Mode()&os.ModeSocket == 0 {
		return errors.New("guest socket path is occupied by a non-socket file")
	}
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("remove stale guest socket: %w", err)
	}
	return nil
}
