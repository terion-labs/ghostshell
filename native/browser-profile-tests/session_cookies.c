// Exercise the shipped CEF boundary in separate processes, without a website,
// real credentials, or any user profile. Cookie values are disposable fixtures.
#include "exclr8cef.h"
#include <stdatomic.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static atomic_int completed;
static atomic_int cookies;
static int operation_timeout_seconds = 15;

static void probe_timed_out(int signal_number) {
    (void)signal_number;
    static const char message[] = "Native cookie probe timed out, including Keychain authorization/shutdown.\n";
    write(STDERR_FILENO, message, sizeof(message) - 1);
    _exit(124);
}

static void visit(int request, int done, const char* name, const char* value,
                  const char* domain, const char* path, int secure, int http_only) {
    (void)request; (void)domain; (void)path; (void)secure; (void)http_only;
    if (done) {
        atomic_store(&completed, 1);
    } else if (strcmp(name, "login") == 0 && strcmp(value, "test-session") == 0) {
        atomic_fetch_add(&cookies, 1);
    }
}

static void flushed(int request, int result) {
    (void)request;
    atomic_store(&completed, result ? 1 : -1);
}

static int wait_for_completion(void) {
    struct timespec start, now;
    clock_gettime(CLOCK_MONOTONIC, &start);
    while (atomic_load(&completed) == 0) {
        excef_do_message_loop_work();
        usleep(5000);
        clock_gettime(CLOCK_MONOTONIC, &now);
        if (now.tv_sec - start.tv_sec >= operation_timeout_seconds) {
            fprintf(stderr, "Timed out waiting for CEF cookie operation\n");
            return 0;
        }
    }
    return atomic_load(&completed) == 1;
}

static int check_cookies(int context, int expected) {
    atomic_store(&completed, 0);
    atomic_store(&cookies, 0);
    if (!excef_get_cookies_in_context(context, "https://session-probe.invalid/", 1)
        || !wait_for_completion()) return 0;
    if (atomic_load(&cookies) != expected) {
        fprintf(stderr, "Expected %d session cookies, found %d\n", expected, atomic_load(&cookies));
        return 0;
    }
    return 1;
}

static int write_cookie(int context) {
    if (!excef_set_cookie_in_context(context, "https://session-probe.invalid/",
            "login", "test-session", "session-probe.invalid", "/", 1, 1)) return 0;
    atomic_store(&completed, 0);
    if (!excef_flush_cookie_store_async(context, 2) || !wait_for_completion()) return 0;
    return check_cookies(context, 1);
}

int main(int argc, char** argv) {
    if (argc != 5 || (strcmp(argv[1], "write") != 0 && strcmp(argv[1], "read") != 0
        && strcmp(argv[1], "read-missing") != 0)
        || (strcmp(argv[4], "native") != 0 && strcmp(argv[4], "mock") != 0)) return 64;
    // OS authorization can also block CEF shutdown after a cookie timeout.
    // Keep the disposable integration probe bounded without accepting access.
    signal(SIGALRM, probe_timed_out);
    alarm(90);
    if (strcmp(argv[4], "native") == 0) operation_timeout_seconds = 60;
    char cache[4096], other[4096];
    const char* profile = strcmp(argv[1], "write") == 0 ? "profile" : "restored-profile";
    if (snprintf(cache, sizeof(cache), "%s/%s", argv[2], profile) >= (int)sizeof(cache)
        || snprintf(other, sizeof(other), "%s/other", argv[2]) >= (int)sizeof(other)) return 64;
    excef_init_settings settings = {0};
    settings.root_cache_path = argv[2];
    settings.persist_session_cookies = 1; // Same global setting as GhostSHELL.
    settings.log_severity = 3; // Surface CEF profile-path errors in the test.
    excef_set_init_settings(&settings);
    // Explicit test-only mode lets noninteractive build hosts validate CEF's
    // persistence ABI without accessing a user's Keychain. The native mode is
    // the production behavior and must be exercised interactively on macOS.
    if (strcmp(argv[4], "mock") == 0)
        excef_add_command_line_switch("use-mock-keychain", NULL);
    if (excef_initialize_offscreen(1, argv, argv[3], NULL) != 0) return 2;
    excef_set_cookie_visit_callback(visit);
    excef_set_request_context_completion_callback(flushed);
    int durable = excef_create_request_context(cache);
    int separate = excef_create_request_context(other);
    int private_context = excef_create_request_context(NULL);
    int success = durable && separate && private_context;
    if (success && strcmp(argv[1], "write") == 0) {
        success = write_cookie(durable) && write_cookie(private_context)
            && check_cookies(separate, 0);
    } else if (success) {
        success = check_cookies(durable, strcmp(argv[1], "read-missing") == 0 ? 0 : 1)
            && check_cookies(separate, 0)
            && check_cookies(private_context, 0);
    }
    excef_release_request_context(private_context);
    excef_release_request_context(separate);
    excef_release_request_context(durable);
    excef_shutdown();
    if (success) printf("Session-cookie %s passed\n", argv[1]);
    return success ? 0 : 1;
}
