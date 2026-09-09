// Native-only WindowProxy/context fixture. All traffic stays on the disposable
// loopback proxy below; all credentials and cookies are synthetic test values.
#include "exclr8cef.h"
#include <CoreFoundation/CoreFoundation.h>
#include <arpa/inet.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static atomic_int stopped, ready[64], loaded[64], closed[64], cookie_seen;
static atomic_int proxy_auth, server_auth, reply_ready;
static atomic_int preference_completed, preference_accepted;
static int owners[64], children[64], last_child, close_during_creation, close_child_during_creation, proxy_port;
static int waiting_message, next_message, reply_success, unexpected_auth;

static void pump(void) {
    excef_do_message_loop_work();
    CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.005, true);
}
static int wait_value(atomic_int* value, int minimum) {
    time_t deadline = time(NULL) + 15;
    while (atomic_load(value) < minimum && time(NULL) < deadline) pump();
    return atomic_load(value) >= minimum;
}
static void paint(int id, const void* buffer, int width, int height) {
    (void)id; (void)buffer; (void)width; (void)height;
}
static void initialized(int id) { if (id < 64) atomic_store(&ready[id], 1); }
static void preference_applied(int request, int result) {
    (void)request;
    atomic_store(&preference_accepted, result);
    atomic_store(&preference_completed, 1);
}
static void load_ended(int id, int main_frame, const char* url, int status) {
    (void)url;
    if (id < 64 && main_frame && status == 200) atomic_fetch_add(&loaded[id], 1);
}
static void browser_closed(int id) {
    if (id >= 64) return;
    atomic_store(&closed[id], 1);
    // Mirrors the managed transient surface's recursive owner lifetime.
    for (int child = 1; child < 64; child++) {
        if (owners[child] == id && !atomic_load(&closed[child])) excef_close_browser(child, 1);
    }
}
static int host_popup(int owner, int child, const char* url, const char* frame, int disposition, int gesture) {
    (void)frame; (void)disposition;
    if (child >= 64 || owner >= 64 || !gesture) return 0;
    if (*url && strcmp(url, "about:blank") != 0) return 0;
    owners[child] = owner;
    children[owner] = child;
    last_child = child;
    fprintf(stderr, "Hosted original popup %d from %d, initial URL '%s'\n", child, owner, url);
    if (close_during_creation) excef_close_browser(owner, 1);
    if (close_child_during_creation) excef_close_browser(child, 1);
    return 1;
}
static void authenticate(int browser, uint64_t token, int proxy, const char* host, int port,
        const char* realm, const char* scheme, const char* origin) {
    if (proxy && strcmp(host, "127.0.0.1") == 0 && port == proxy_port && strcmp(scheme, "basic") == 0) {
        atomic_fetch_add(&proxy_auth, 1);
        excef_resolve_auth(token, "proxy", "fixture");
    } else if (!proxy && browser < 64 && owners[browser] > 0
        && strcmp(host, "fixture.invalid") == 0 && port == 80
        && strcmp(realm, "fixture") == 0 && strcmp(scheme, "basic") == 0
        && strcmp(origin, "http://fixture.invalid/auth") == 0) {
        atomic_fetch_add(&server_auth, 1);
        excef_resolve_auth(token, "fixture", "fixture");
    } else {
        unexpected_auth = 1;
        excef_resolve_auth(token, NULL, NULL);
    }
}
static void devtools(int browser, int event, int message, const char* json) {
    (void)browser;
    if (event || message != waiting_message) return;
    reply_success = strstr(json, "\"value\":true") != NULL || strstr(json, "\"value\": true") != NULL;
    if (!reply_success) fprintf(stderr, "Fixture evaluation: %s\n", json);
    atomic_store(&reply_ready, 1);
}
static int evaluate(int browser, const char* expression) {
    char parameters[4096];
    snprintf(parameters, sizeof(parameters),
        "{\"expression\":\"%s\",\"returnByValue\":true,\"userGesture\":true}", expression);
    waiting_message = ++next_message;
    atomic_store(&reply_ready, 0);
    return excef_execute_devtools_method(browser, waiting_message, "Runtime.evaluate", parameters)
        && wait_value(&reply_ready, 1) && reply_success;
}
static int eventually(int browser, const char* expression) {
    time_t deadline = time(NULL) + 10;
    while (time(NULL) < deadline) {
        if (evaluate(browser, expression)) return 1;
        for (int i = 0; i < 10; i++) pump();
    }
    return 0;
}
static void* serve(void* argument) {
    int listener = *(int*)argument;
    while (!atomic_load(&stopped)) {
        int client = accept(listener, NULL, NULL);
        if (client < 0) break;
        struct timeval timeout = { .tv_sec = 2 };
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
        char request[16 * 1024] = {0};
        ssize_t count = recv(client, request, sizeof(request) - 1, 0);
        if (count > 0) {
            const char* status = "200 OK";
            const char* authentication = "";
            const char* body = "<!doctype html><html><body>Popup fixture<script>window.messages=[];window.onmessage=e=>messages.push(e.data);if(window.opener)opener.postMessage('ready',location.origin);</script></body></html>";
            if (!strstr(request, "Proxy-Authorization: Basic cHJveHk6Zml4dHVyZQ==")) {
                status = "407 Proxy Authentication Required";
                authentication = "Proxy-Authenticate: Basic realm=\"proxy-fixture\"\r\n";
                body = "";
            } else if (strstr(request, "http://fixture.invalid/auth ")
                && !strstr(request, "Authorization: Basic Zml4dHVyZTpmaXh0dXJl")) {
                status = "401 Unauthorized";
                authentication = "WWW-Authenticate: Basic realm=\"fixture\"\r\n";
                body = "";
            }
            if (strstr(request, "http://fixture.invalid/child ") && strstr(request, "fixture=shared"))
                atomic_store(&cookie_seen, 1);
            char response[4096];
            int length = snprintf(response, sizeof(response),
                "HTTP/1.1 %s\r\n%sContent-Type: text/html\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n%s",
                status, authentication, strlen(body), body);
            send(client, response, (size_t)length, 0);
        }
        close(client);
    }
    return NULL;
}
static int create_parent(int context) {
    int browser = excef_create_offscreen_browser_in_context(900, 700, 1, "about:blank", paint, context);
    if (browser <= 0 || browser >= 64 || !wait_value(&ready[browser], 1)) return 0;
    excef_load_url(browser, "http://fixture.invalid/");
    return wait_value(&loaded[browser], 1) ? browser : 0;
}
int main(int argc, char** argv) {
    if (argc != 3) return 64;
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (listener < 0 || bind(listener, (struct sockaddr*)&address, sizeof(address)) || listen(listener, 16)) return 2;
    socklen_t size = sizeof(address);
    if (getsockname(listener, (struct sockaddr*)&address, &size)) return 2;
    proxy_port = ntohs(address.sin_port);
    pthread_t server;
    if (pthread_create(&server, NULL, serve, &listener)) return 2;
    excef_init_settings settings = {0};
    settings.root_cache_path = argv[1]; settings.log_severity = 3;
    excef_set_init_settings(&settings);
    excef_add_command_line_switch("use-mock-keychain", NULL);
    // Production setting: route authentication belongs to CEF, not Chrome UI.
    excef_add_command_line_switch("disable-chrome-login-prompt", NULL);
    if (excef_initialize_offscreen(1, argv, argv[2], NULL)) return 3;
    excef_set_browser_initialized_callback(initialized);
    excef_set_load_end_callback(load_ended);
    excef_set_browser_closed_callback(browser_closed);
    excef_set_host_popup_callback(host_popup);
    excef_set_auth_request_callback_v2(authenticate);
    excef_set_devtools_message_callback(devtools);
    excef_set_request_context_completion_callback(preference_applied);
    char cache[4096];
    if (snprintf(cache, sizeof(cache), "%s/proxied-profile", argv[1]) >= (int)sizeof(cache)) return 64;
    int context = excef_create_request_context(cache);
    char proxy[256];
    snprintf(proxy, sizeof(proxy), "{\"mode\":\"fixed_servers\",\"server\":\"http://127.0.0.1:%d\",\"bypass_list\":\"<-loopback>\"}", proxy_port);
    if (!excef_set_preference_async(context, "proxy", proxy, 1)
        || !wait_value(&preference_completed, 1) || !atomic_load(&preference_accepted)) return 4;
    atomic_store(&preference_completed, 0);
    if (!excef_set_preference_async(context, "asura.invalid.preference", "true", 2)
        || !wait_value(&preference_completed, 1) || atomic_load(&preference_accepted)) return 4;
    int parent = create_parent(context), success = parent > 0;
    if (success) success = evaluate(parent, "document.cookie='fixture=shared; path=/'; document.cookie.includes('fixture=shared')");
    if (success) success = evaluate(parent, "window.popup=window.open('', 'fixture'); !!window.popup && popup.opener===window");
    int child = children[parent];
    if (success) success = child > 0 && wait_value(&ready[child], 1);
    if (success) success = evaluate(parent, "popup.location='/child'; true") && wait_value(&loaded[child], 1);
    if (success) success = eventually(parent, "messages.includes('ready')") && atomic_load(&cookie_seen);
    if (success) success = evaluate(parent, "popup.postMessage('reply',location.origin); true") && eventually(child, "messages.includes('reply')");
    if (success) success = evaluate(parent, "popup.location='/auth'; true") && wait_value(&loaded[child], 2) && atomic_load(&server_auth);
    if (success) success = evaluate(child, "window.nested=window.open('', 'nested'); !!nested && nested.opener===window");
    int nested = children[child];
    if (success) success = nested > 0 && wait_value(&ready[nested], 1);
    if (success) {
        excef_execute_javascript(nested, "window.close()", "");
        success = wait_value(&closed[nested], 1);
    }
    if (parent > 0) {
        excef_close_browser(parent, 1);
        success = wait_value(&closed[parent], 1) && child > 0 && wait_value(&closed[child], 1) && success;
    }
    if (success) {
        parent = create_parent(context);
        close_during_creation = 1;
        waiting_message = ++next_message;
        excef_execute_devtools_method(parent, waiting_message, "Runtime.evaluate", "{\"expression\":\"window.open('')\",\"userGesture\":true}");
        success = wait_value(&closed[parent], 1) && last_child > 0 && wait_value(&closed[last_child], 1);
    }
    if (success) {
        close_during_creation = 0;
        parent = create_parent(context);
        close_child_during_creation = 1;
        success = evaluate(parent, "window.popup=window.open(''); true");
        child = children[parent];
        success = child > 0 && wait_value(&closed[child], 1) && success;
        if (success) success = evaluate(parent, "document.body !== null");
        excef_close_browser(parent, 1);
        success = wait_value(&closed[parent], 1) && success;
    }
    success = success && atomic_load(&proxy_auth) > 0 && !unexpected_auth;
    fprintf(stderr, "Native hosted popup WindowProxy/context/lifecycle: %s\n", success ? "passed" : "FAILED");
    for (int id = 1; id < 64; id++) if (atomic_load(&ready[id]) && !atomic_load(&closed[id])) excef_close_browser(id, 1);
    for (int i = 0; i < 50; i++) pump();
    excef_release_request_context(context);
    excef_shutdown();
    atomic_store(&stopped, 1); shutdown(listener, SHUT_RDWR); close(listener); pthread_join(server, NULL);
    return success ? 0 : 1;
}
