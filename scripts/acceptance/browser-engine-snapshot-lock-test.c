// macOS-only regression for the platform snapshot primitive. Compile with
// clang -Wall -Wextra -Werror and pass a new private temporary directory.
// Parent source descriptors must not be opened/closed by the archive reader:
// Darwin releases this process's fcntl locks when any such descriptor closes.
#include <sys/file.h>
#include <sys/wait.h>
#include <fcntl.h>
#include <signal.h>
#include <unistd.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int await_child(pid_t child) {
  for (int i = 0; i < 1000; ++i) {
    int status = 0;
    pid_t result = waitpid(child, &status, WNOHANG);
    if (result == child) return WIFEXITED(status) ? WEXITSTATUS(status) : 126;
    if (result < 0 && errno != EINTR) return 125;
    usleep(5000);
  }
  kill(child, SIGKILL);
  while (waitpid(child, NULL, 0) < 0 && errno == EINTR) { }
  return 124;
}

static int remains_locked(const char *path, int inherited) {
  pid_t child = fork();
  if (child < 0) return 0;
  if (child == 0) {
    close(inherited);
    int fd = open(path, O_RDONLY);
    if (fd < 0) _exit(2);
    int result = flock(fd, LOCK_SH | LOCK_NB);
    int locked = result < 0 && errno == EWOULDBLOCK;
    close(fd);
    _exit(locked ? 0 : 1);
  }
  return await_child(child) == 0;
}

int main(int argc, char **argv) {
  if (argc != 2) return 2;
  char source[4096], destination[4096];
  if (snprintf(source, sizeof(source), "%s/first_party_sets.db", argv[1]) >= (int)sizeof(source)
      || snprintf(destination, sizeof(destination), "%s/copied.db", argv[1]) >= (int)sizeof(destination)) return 2;
  int fd = open(source, O_CREAT | O_EXCL | O_RDWR, 0600);
  if (fd < 0) return 3;
  const char bytes[] = "synthetic database snapshot bytes";
  if (write(fd, bytes, sizeof(bytes)) != sizeof(bytes)) return 4;
  struct flock lock = {.l_start = 0x40000000, .l_len = 512,
                       .l_type = F_WRLCK, .l_whence = SEEK_SET};
  if (fcntl(fd, F_SETLK, &lock) != 0 || !remains_locked(source, fd)) return 5;

  pid_t child = fork();
  if (child < 0) return 6;
  if (child == 0) {
    close(fd);
    int sink = open("/dev/null", O_RDWR);
    if (sink < 0) _exit(125);
    dup2(sink, STDOUT_FILENO);
    dup2(sink, STDERR_FILENO);
    close(sink);
    execl("/bin/cp", "/bin/cp", "-X", source, destination, NULL);
    _exit(127);
  }
  if (await_child(child) != 0 || !remains_locked(source, fd)) return 7;
  int copy = open(destination, O_RDONLY);
  if (copy < 0 || flock(copy, LOCK_SH | LOCK_NB) != 0) return 8;
  char actual[sizeof(bytes)];
  if (read(copy, actual, sizeof(actual)) != sizeof(actual)
      || memcmp(bytes, actual, sizeof(bytes)) != 0) return 9;
  close(copy);
  if (!remains_locked(source, fd)) return 10;

  // Negative control: exactly the failed parent FileStream pattern invalidates
  // the retained lock. This prevents an inert lock fixture from passing.
  int other = open(source, O_RDONLY);
  if (other < 0 || flock(other, LOCK_SH | LOCK_NB) == 0 || errno != EWOULDBLOCK) return 11;
  close(other);
  if (remains_locked(source, fd)) return 12;
  close(fd);
  puts("PASS platform snapshot preserves bytes and parent fcntl lock; parent read-close control releases it");
  return 0;
}
