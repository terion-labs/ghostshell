// Disposable loopback probe, supporting both old and new callback ABIs.
#include "exclr8cef.h"
#include <CoreFoundation/CoreFoundation.h>
#include <arpa/inet.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
static atomic_int completed;
static char origin[128];
static int port;
static void paint(int browser, const void* buffer, int width, int height) {
    (void)browser; (void)buffer; (void)width; (void)height;
}
static void authenticate(int browser, uint64_t token, int proxy, const char* host,
        int challenge_port, const char* realm, const char* scheme, const char* challenge_origin) {
    (void)browser;
    int valid = !proxy && strcmp(host, "127.0.0.1") == 0 && challenge_port == port
        && strcmp(realm, "fixture") == 0 && strcmp(scheme, "basic") == 0
        && (!challenge_origin || strcmp(challenge_origin, origin) == 0);
    fprintf(stderr, "Auth callback: proxy=%d origin=%s valid=%d\n", proxy,
        challenge_origin ? challenge_origin : "legacy-unavailable", valid);
    excef_resolve_auth(token, NULL, NULL);
    atomic_store(&completed, valid ? 1 : -1);
}
static void authenticate_legacy(int browser, uint64_t token, int proxy, const char* host,
        int challenge_port, const char* realm, const char* scheme) {
    authenticate(browser, token, proxy, host, challenge_port, realm, scheme, NULL);
}
static void closed(int browser) { (void)browser; atomic_store(&completed, 1); }
static int wait_for_completion(void) {
    time_t deadline = time(NULL) + 15;
    while (!atomic_load(&completed) && time(NULL) < deadline) {
        excef_do_message_loop_work();
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.005, true);
    }
    return atomic_load(&completed) == 1;
}
static void* serve(void* argument) {
    int client = accept(*(int*)argument, NULL, NULL);
    if (client >= 0) {
        struct timeval timeout = { .tv_sec = 5 };
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
        char request[2048];
        if (recv(client, request, sizeof(request), 0) > 0) {
            fprintf(stderr, "Loopback request received\n");
            const char response[] = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"fixture\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            send(client, response, sizeof(response) - 1, 0);
        }
        close(client);
    }
    return NULL;
}
int main(int argc, char** argv) {
    if (argc != 4) return 64;
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (listener < 0 || bind(listener, (struct sockaddr*)&address, sizeof(address)) || listen(listener, 1)) return 2;
    socklen_t size = sizeof(address);
    if (getsockname(listener, (struct sockaddr*)&address, &size)) return 2;
    port = ntohs(address.sin_port);
    snprintf(origin, sizeof(origin), "http://127.0.0.1:%d/protected", port);
    pthread_t server;
    if (pthread_create(&server, NULL, serve, &listener)) return 2;
    excef_init_settings settings = {0};
    settings.root_cache_path = argv[1];
    settings.log_severity = 3;
    excef_set_init_settings(&settings);
    excef_add_command_line_switch("use-mock-keychain", NULL);
    // Match BrowserEngineRuntime: otherwise Chrome owns the auth dialog and
    // deliberately bypasses CEF's GetAuthCredentials callback.
    excef_add_command_line_switch("disable-chrome-login-prompt", NULL);
    if (excef_initialize_offscreen(1, argv, argv[2], NULL)) return 3;
    if (strcmp(argv[3], "v2") == 0) {
        void (*set_callback)(excef_auth_request_cb_t) = dlsym(RTLD_DEFAULT, "excef_set_auth_request_callback_v2");
        if (!set_callback) return 4;
        set_callback(authenticate);
    } else {
        void (*set_callback)(void (*)(int, uint64_t, int, const char*, int, const char*, const char*))
            = dlsym(RTLD_DEFAULT, "excef_set_auth_request_callback");
        if (!set_callback) return 4;
        set_callback(authenticate_legacy);
    }
    excef_set_browser_closed_callback(closed);
    int context = excef_create_request_context(NULL);
    int browser = excef_create_offscreen_browser_in_context(320, 240, 1, "about:blank", paint, context);
    for (int i = 0; i < 50; i++) {
        excef_do_message_loop_work();
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.005, true);
    }
    excef_load_url(browser, origin);
    int success = browser > 0 && wait_for_completion();
    fprintf(stderr, "Native auth navigation result: %d\n", success);
    if (browser > 0) {
        atomic_store(&completed, 0);
        excef_close_browser(browser, 1);
        if (!wait_for_completion()) fprintf(stderr, "Browser close timed out\n");
    }
    shutdown(listener, SHUT_RDWR); close(listener); pthread_join(server, NULL);
    excef_release_request_context(context);
    excef_shutdown();
    return success ? 0 : 1;
}
