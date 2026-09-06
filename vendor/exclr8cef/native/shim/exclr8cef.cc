#include "exclr8cef.h"

#include <cstdio>
#include <cstring>
#include <string>

#include "include/cef_app.h"
#include "include/cef_version.h"

#include "exclr8cef_app.h"

#if defined(_WIN32)
#  include <windows.h>
#endif

namespace exclr8cef {

// Host-provided init settings (set via excef_set_init_settings before init).
// Stored by value so the host's pointers don't need to outlive the call.
struct StashedInitSettings {
    bool set = false;
    std::string cache_path;
    std::string root_cache_path;
    std::string user_agent;
    std::string user_agent_product;
    std::string locale;
    std::string accept_language_list;
    std::string log_file;
    std::string javascript_flags;
    int log_severity = 0;
    bool persist_session_cookies = false;
    int remote_debugging_port = 0;
    bool enable_javascript_bridge = false;
};
StashedInitSettings g_init_settings;

// Apply host-provided settings to a CefSettings instance. Called from every
// init path. Security-sensitive fields are set unconditionally before optional
// host values are overlaid.
void ApplyHostInitSettings(CefSettings& settings) {
    // Ignore arbitrary Chromium/CEF flags supplied to the host process. The
    // explicit AddCommandLineSwitch API remains available for trusted host
    // configuration in OnBeforeCommandLineProcessing.
    settings.command_line_args_disabled = true;

    // USE_SANDBOX is ON by default in CEF's CMake. Only explicitly-unsandboxed
    // builds receive --no-sandbox. Windows CEF 138+ bootstrap builds are
    // rejected at CMake configure time because a .NET host cannot receive the
    // bootstrap-owned sandbox_info pointer through this C ABI.
#if !defined(CEF_USE_SANDBOX)
    settings.no_sandbox = true;
#endif

    const auto& s = g_init_settings;
    if (!s.set) return;
    if (!s.cache_path.empty())
        CefString(&settings.cache_path).FromString(s.cache_path);
    if (!s.root_cache_path.empty())
        CefString(&settings.root_cache_path).FromString(s.root_cache_path);
    if (!s.user_agent.empty())
        CefString(&settings.user_agent).FromString(s.user_agent);
    if (!s.user_agent_product.empty())
        CefString(&settings.user_agent_product).FromString(s.user_agent_product);
    if (!s.locale.empty())
        CefString(&settings.locale).FromString(s.locale);
    if (!s.accept_language_list.empty())
        CefString(&settings.accept_language_list).FromString(s.accept_language_list);
    if (!s.log_file.empty())
        CefString(&settings.log_file).FromString(s.log_file);
    if (!s.javascript_flags.empty())
        CefString(&settings.javascript_flags).FromString(s.javascript_flags);
    if (s.log_severity != 0)
        settings.log_severity = static_cast<cef_log_severity_t>(s.log_severity);
    if (s.persist_session_cookies)
        settings.persist_session_cookies = true;
    if (s.remote_debugging_port > 0)
        settings.remote_debugging_port = s.remote_debugging_port;
}

}  // namespace exclr8cef

extern "C" void excef_set_init_settings(const excef_init_settings* in) {
    if (!in) { exclr8cef::g_init_settings = exclr8cef::StashedInitSettings{}; return; }
    exclr8cef::StashedInitSettings s;
    s.set = true;
    if (in->cache_path)             s.cache_path = in->cache_path;
    if (in->root_cache_path)        s.root_cache_path = in->root_cache_path;
    if (in->user_agent)             s.user_agent = in->user_agent;
    if (in->user_agent_product)     s.user_agent_product = in->user_agent_product;
    if (in->locale)                 s.locale = in->locale;
    if (in->accept_language_list)   s.accept_language_list = in->accept_language_list;
    if (in->log_file)               s.log_file = in->log_file;
    if (in->javascript_flags)       s.javascript_flags = in->javascript_flags;
    s.log_severity = in->log_severity;
    s.persist_session_cookies = in->persist_session_cookies != 0;
    s.remote_debugging_port = in->remote_debugging_port;
    s.enable_javascript_bridge = in->enable_javascript_bridge != 0;
    exclr8cef::g_init_settings = std::move(s);
}

namespace exclr8cef {

bool JavascriptBridgeEnabled() {
    return g_init_settings.enable_javascript_bridge;
}

}  // namespace exclr8cef

namespace {

constexpr const char kShimVersion[] = "0.8.0-ghostshell.7";

void copy_to(char* dst, size_t dst_size, const char* src) {
    if (!dst || dst_size == 0) return;
    std::strncpy(dst, src, dst_size - 1);
    dst[dst_size - 1] = '\0';
}

#if !defined(__APPLE__)
// CefMainArgs has different constructors per platform. On Windows it takes
// HINSTANCE; on Linux it takes argc/argv. Wrap so the rest of the code
// stays platform-agnostic.
static CefMainArgs make_main_args(int argc, char** argv) {
#  if defined(_WIN32)
    (void)argc; (void)argv;
    return CefMainArgs(GetModuleHandle(nullptr));
#  else
    return CefMainArgs(argc, argv);
#  endif
}

#endif

}  // namespace

extern "C" void excef_get_versions(excef_versions* out) {
    if (!out) return;

    copy_to(out->shim_version, EXCEF_VERSION_BUFFER_SIZE, kShimVersion);

    char cef_buf[EXCEF_VERSION_BUFFER_SIZE];
    std::snprintf(cef_buf, sizeof(cef_buf), "%d.%d.%d",
                  CEF_VERSION_MAJOR, CEF_VERSION_MINOR, CEF_VERSION_PATCH);
    copy_to(out->cef_version, EXCEF_VERSION_BUFFER_SIZE, cef_buf);

    char chrome_buf[EXCEF_VERSION_BUFFER_SIZE];
    std::snprintf(chrome_buf, sizeof(chrome_buf), "%d.%d.%d.%d",
                  CHROME_VERSION_MAJOR, CHROME_VERSION_MINOR,
                  CHROME_VERSION_BUILD, CHROME_VERSION_PATCH);
    copy_to(out->chromium_version, EXCEF_VERSION_BUFFER_SIZE, chrome_buf);
}

extern "C" int excef_create_browser(const char* url) {
    if (!url) return 1;
    exclr8cef::SetPendingBrowserUrl(url);
    return 0;
}

extern "C" void excef_quit_message_loop(void) {
    CefQuitMessageLoop();
}

extern "C" void excef_add_command_line_switch(const char* name, const char* value) {
    if (!name || !*name) return;
    exclr8cef::AddExtraCommandLineSwitch(name, value ? value : "");
}

// ---- Lifecycle (Windows/Linux) -------------------------------------------
//
// macOS-specific implementations live in exclr8cef_mac.mm because they
// need CefScopedLibraryLoader + an NSApplication subclass. On Windows
// and Linux libcef is linked normally and the host owns the run loop.

#if !defined(__APPLE__)

extern "C" int excef_execute_process(int argc, char** argv) {
    auto main_args = make_main_args(argc, argv);
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    return CefExecuteProcess(main_args, app, nullptr);
}

extern "C" int excef_initialize(int argc, char** argv,
                                const char* subprocess_path) {
    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
    }
    exclr8cef::ApplyHostInitSettings(settings);
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 1;
    return 0;
}

extern "C" int excef_initialize_external_pump(int argc, char** argv,
                                              const char* subprocess_path,
                                              excef_schedule_pump_work_t cb) {
    exclr8cef::SetSchedulePumpCallback(
        reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    settings.external_message_pump = true;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
    }
    exclr8cef::ApplyHostInitSettings(settings);
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 2;
    return 0;
}

extern "C" int excef_initialize_offscreen(int argc, char** argv,
                                          const char* subprocess_path,
                                          excef_schedule_pump_work_t cb) {
    exclr8cef::SetSchedulePumpCallback(
        reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    settings.external_message_pump = true;
    settings.windowless_rendering_enabled = true;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
    }
    exclr8cef::ApplyHostInitSettings(settings);
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 2;
    return 0;
}

extern "C" void excef_run_message_loop(void) {
    CefRunMessageLoop();
}

extern "C" void excef_do_message_loop_work(void) {
    CefDoMessageLoopWork();
}

extern "C" void excef_shutdown(void) {
    CefShutdown();
    exclr8cef::ResetApp();
    exclr8cef::SetSchedulePumpCallback(nullptr);
}

#endif  // !__APPLE__
