#include <limits.h>
#include <sys/resource.h>
#include <fcntl.h>
#include <stdio.h>
#if defined(__APPLE__)
#include <sys/sysctl.h>
#else
#include <sys/syscall.h>
#endif

/* Compute the complete descriptor ceiling before fork, when libc is safe.
 * The child performs only async-signal-safe close/syscall operations. */
static int asura_descriptor_ceiling(void) {
    struct rlimit limit;
    if (getrlimit(RLIMIT_NOFILE, &limit) != 0) return -1;
#if defined(__APPLE__)
    int ceiling = 0;
    size_t size = sizeof(ceiling);
    int name[] = { CTL_KERN, KERN_MAXFILESPERPROC };
    if (sysctl(name, 2, &ceiling, &size, NULL, 0) != 0 || ceiling <= 0) return -1;
    if (limit.rlim_max != RLIM_INFINITY && limit.rlim_max < INT_MAX
        && (int)limit.rlim_max > ceiling) ceiling = (int)limit.rlim_max;
    return ceiling;
#else
    /* RLIMIT_NOFILE can be lowered after a higher descriptor was opened. The
     * kernel's process ceiling remains authoritative for that existing handle. */
    FILE* source = fopen("/proc/sys/fs/nr_open", "r");
    if (source == NULL) return -1;
    unsigned long long ceiling = 0;
    char trailing;
    int fields = fscanf(source, "%llu %c", &ceiling, &trailing);
    int close_result = fclose(source);
    if (fields != 1 || close_result != 0 || ceiling < 3 || ceiling > INT_MAX) {
        errno = EINVAL;
        return -1;
    }
    if (limit.rlim_max != RLIM_INFINITY && limit.rlim_max > ceiling) {
        if (limit.rlim_max > INT_MAX) { errno = EOVERFLOW; return -1; }
        ceiling = limit.rlim_max;
    }
    return (int)ceiling;
#endif
}

static void asura_close_child_descriptors(int ceiling) {
#if !defined(__APPLE__) && defined(SYS_close_range)
    if (syscall(SYS_close_range, 3u, UINT_MAX, 0u) == 0) return;
    if (errno != ENOSYS && errno != EINVAL) _exit(errno);
#endif
    /* Older kernels retain support without an arbitrary descriptor cap. */
    for (int fd = 3; fd < ceiling; ++fd) close(fd);
}

PTY_EXPORT int asura_pty_descriptor_boundary_abi(void) { return 1; }
