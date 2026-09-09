#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

typedef struct { int master_fd; int pid; int error; } spawn_result;
extern spawn_result pty_spawn(const char*, char* const[], char* const[], const char*, const void*, const void*);
extern int asura_pty_descriptor_boundary_abi(void);

static void require(int condition, const char* detail) {
    if (!condition) { fprintf(stderr, "FAIL: %s (errno=%d)\n", detail, errno); exit(1); }
}

int main(int argc, char** argv) {
    if (argc == 3 && strcmp(argv[1], "child") == 0) {
        int inherited = atoi(argv[2]);
        require(fcntl(inherited, F_GETFD) == -1 && errno == EBADF, "parent descriptor inherited");
        require(isatty(0) && isatty(1) && isatty(2), "PTY standard descriptors missing");
        puts("READY"); fflush(stdout);
        char input[32];
        require(fgets(input, sizeof(input), stdin) != NULL, "PTY input unavailable");
        require(strcmp(input, "ping\n") == 0, "PTY input changed");
        return 0;
    }
    require(asura_pty_descriptor_boundary_abi() == 1, "ABI marker");
    struct rlimit available;
    require(getrlimit(RLIMIT_NOFILE, &available) == 0, "descriptor limit");
    if (available.rlim_cur <= 4096) {
        require(available.rlim_max >= 4097, "high descriptor test requires hard limit >= 4097");
        available.rlim_cur = 4097;
        require(setrlimit(RLIMIT_NOFILE, &available) == 0, "raise soft limit for high descriptor test");
    }
    char path[] = "/tmp/asura-pty-lock-XXXXXX";
    int file = mkstemp(path);
    require(file >= 0, "private test file");
    int inherited = fcntl(file, F_DUPFD, 4096);
    require(inherited >= 4096, "high descriptor allocation");
    require(fcntl(inherited, F_SETFD, 0) == 0, "explicitly inheritable descriptor");
    require(flock(inherited, LOCK_EX | LOCK_NB) == 0, "parent lock");
    close(file);
#if defined(__linux__)
    /* Existing descriptors survive lowering even the hard limit. */
    struct rlimit lowered = { 512, 512 };
    require(setrlimit(RLIMIT_NOFILE, &lowered) == 0, "lower descriptor limit");
#endif
    char number[32]; snprintf(number, sizeof(number), "%d", inherited);
    char* child_args[] = { argv[0], "child", number, NULL };
    struct timespec start, end;
    clock_gettime(CLOCK_MONOTONIC, &start);
    spawn_result child = pty_spawn(argv[0], child_args, NULL, NULL, NULL, NULL);
    require(child.pid > 0, "spawn");
    require((fcntl(child.master_fd, F_GETFD) & FD_CLOEXEC) != 0, "master must be close-on-exec");
    close(inherited);
    struct pollfd ready = { child.master_fd, POLLIN, 0 };
    require(poll(&ready, 1, 3000) > 0, "startup deadline");
    char output[256] = {0};
    require(read(child.master_fd, output, sizeof(output) - 1) > 0 && strstr(output, "READY"), "child descriptor proof");
    clock_gettime(CLOCK_MONOTONIC, &end);
    double milliseconds = (end.tv_sec - start.tv_sec) * 1000.0 + (end.tv_nsec - start.tv_nsec) / 1000000.0;
    int reopened = open(path, O_RDWR);
    require(reopened >= 0 && flock(reopened, LOCK_EX | LOCK_NB) == 0, "child retained parent lock");
    require(write(child.master_fd, "ping\n", 5) == 5, "PTY write");
    int status = 0;
    require(waitpid(child.pid, &status, 0) == child.pid && WIFEXITED(status) && WEXITSTATUS(status) == 0, "child result");
    close(child.master_fd); close(reopened); unlink(path);
    printf("PASS descriptor exclusion, lock release, PTY I/O; startup %.2f ms\n", milliseconds);
    return 0;
}
