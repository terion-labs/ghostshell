#include "exclr8cef_osr.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <climits>
#include <cstring>
#include <filesystem>
#include <functional>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "include/base/cef_callback.h"
#include "include/cef_browser.h"
#include "include/cef_cookie.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_frame.h"
#include "include/cef_parser.h"
#include "include/cef_pack_strings.h"
#include "include/cef_process_message.h"
#include "include/cef_request_context.h"
#include "include/cef_resource_bundle.h"
#include "include/cef_resource_handler.h"
#include "include/cef_response.h"
#include "include/cef_scheme.h"
#include "include/cef_task.h"
#include "include/wrapper/cef_closure_task.h"

#include "exclr8cef_app.h"  // GetRegisteredSchemeNames

namespace exclr8cef {

constexpr int kOpenLinkInNewTabCommandId = MENU_ID_USER_FIRST;

namespace {

std::atomic<int> g_next_id{1};
// Guards g_osr_browsers. The map is touched from the host's caller thread
// (every C ABI entry point), the CEF UI thread (OnBeforeClose erase), and
// the CEF IO thread (SchemeFactory::Create reverse lookup) — it MUST be
// locked like every other registry in this file.
std::mutex g_osr_browsers_mu;
std::map<int, CefRefPtr<Exclr8CefOsrHandler>> g_osr_browsers;

// Per-browser request contexts (multi-profile / incognito story).
// Handle 0 is reserved for "use the global default context".
std::atomic<int> g_next_context_handle{1};
std::mutex g_request_contexts_mu;
std::map<int, CefRefPtr<CefRequestContext>> g_request_contexts;

excef_address_change_cb_t g_address_cb = nullptr;
excef_title_change_cb_t g_title_cb = nullptr;
excef_loading_state_cb_t g_loading_state_cb = nullptr;
excef_eval_result_cb_t g_eval_result_cb = nullptr;
excef_cookie_visit_cb_t g_cookie_visit_cb = nullptr;
excef_browser_closed_cb_t g_browser_closed_cb = nullptr;
excef_cursor_change_cb_t g_cursor_change_cb = nullptr;
excef_console_message_cb_t g_console_message_cb = nullptr;
excef_load_start_cb_t g_load_start_cb = nullptr;
excef_load_end_cb_t g_load_end_cb = nullptr;
excef_load_error_cb_t g_load_error_cb = nullptr;
excef_loading_progress_cb_t g_loading_progress_cb = nullptr;
excef_status_message_cb_t g_status_message_cb = nullptr;
excef_tooltip_cb_t g_tooltip_cb = nullptr;
excef_favicon_cb_t g_favicon_cb = nullptr;
excef_fullscreen_cb_t g_fullscreen_cb = nullptr;
excef_browser_initialized_cb_t g_browser_initialized_cb = nullptr;
excef_scroll_offset_cb_t g_scroll_offset_cb = nullptr;
excef_auto_resize_cb_t g_auto_resize_cb = nullptr;
excef_js_dialog_cb_t g_js_dialog_cb = nullptr;
excef_file_dialog_cb_t g_file_dialog_cb = nullptr;
excef_context_menu_cb_t g_context_menu_cb = nullptr;
excef_download_starting_cb_t g_download_starting_cb = nullptr;
excef_download_progress_cb_t g_download_progress_cb = nullptr;
excef_auth_request_cb_t g_auth_request_cb = nullptr;
excef_find_result_cb_t g_find_result_cb = nullptr;
excef_render_process_gone_cb_t g_render_process_gone_cb = nullptr;
excef_scheme_request_cb_t g_scheme_request_cb = nullptr;
excef_resource_request_cb_t g_resource_request_cb = nullptr;
excef_before_browse_cb_t g_before_browse_cb = nullptr;
excef_popup_show_cb_t g_popup_show_cb = nullptr;
excef_popup_size_cb_t g_popup_size_cb = nullptr;
excef_popup_paint_cb_t g_popup_paint_cb = nullptr;
excef_js_invoke_cb_t g_js_invoke_cb = nullptr;
excef_accessibility_tree_cb_t g_a11y_tree_cb = nullptr;
excef_accessibility_location_cb_t g_a11y_location_cb = nullptr;
excef_start_drag_cb_t g_start_drag_cb = nullptr;
excef_drag_image_cb_t g_drag_image_cb = nullptr;
excef_permission_prompt_cb_t g_permission_prompt_cb = nullptr;
excef_media_access_cb_t g_media_access_cb = nullptr;
excef_take_focus_cb_t g_take_focus_cb = nullptr;
excef_set_focus_cb_t g_set_focus_cb = nullptr;
excef_got_focus_cb_t g_got_focus_cb = nullptr;
excef_pre_key_cb_t g_pre_key_cb = nullptr;
excef_key_event_cb_t g_key_event_cb = nullptr;
excef_devtools_message_cb_t g_devtools_message_cb = nullptr;
excef_string_visitor_cb_t g_string_visitor_cb = nullptr;
excef_nav_entry_cb_t g_nav_entry_cb = nullptr;
// Per-browser DevTools observer registrations (keep alive while open).
std::map<int, CefRefPtr<CefRegistration>> g_devtools_observers;
std::mutex g_devtools_observers_mu;
excef_before_popup_cb_t g_before_popup_cb = nullptr;
excef_cert_error_cb_t g_cert_error_cb = nullptr;
excef_frame_lifecycle_cb_t g_frame_lifecycle_cb = nullptr;
excef_main_frame_changed_cb_t g_main_frame_changed_cb = nullptr;
excef_audio_stream_started_cb_t g_audio_started_cb = nullptr;
excef_audio_stream_packet_cb_t g_audio_packet_cb = nullptr;
excef_audio_stream_stopped_cb_t g_audio_stopped_cb = nullptr;
excef_audio_stream_error_cb_t g_audio_error_cb = nullptr;
// Per-browser audio-capture enable flag (set via excef_enable_audio_capture).
std::map<int, bool> g_audio_enabled;
std::mutex g_audio_enabled_mu;
excef_should_filter_response_cb_t g_should_filter_cb = nullptr;
excef_response_filter_cb_t g_response_filter_cb = nullptr;
excef_response_filter_finalize_cb_t g_response_filter_finalize_cb = nullptr;
excef_chrome_command_cb_t g_chrome_command_cb = nullptr;
excef_app_menu_visibility_cb_t g_app_menu_visible_cb = nullptr;
excef_app_menu_visibility_cb_t g_app_menu_enabled_cb = nullptr;
excef_page_action_visibility_cb_t g_page_action_visible_cb = nullptr;
excef_toolbar_button_visibility_cb_t g_toolbar_button_visible_cb = nullptr;
excef_should_handle_resource_cb_t g_should_handle_resource_cb = nullptr;
excef_touch_handle_size_cb_t g_touch_handle_size_cb = nullptr;
excef_touch_handle_state_cb_t g_touch_handle_state_cb = nullptr;
excef_ime_composition_range_cb_t g_ime_composition_range_cb = nullptr;
excef_virtual_keyboard_cb_t g_virtual_keyboard_cb = nullptr;
excef_accelerated_paint_cb_t g_accelerated_paint_cb = nullptr;
excef_request_context_completion_cb_t g_request_context_completion_cb = nullptr;

// Deferred-response registries (one per callback type — the CEF callback
// shape differs across handlers). The owning browser_id lets OnBeforeClose
// cancel any pending entries for the closing browser, rather than leaving
// the CEF callback object refcounted indefinitely.
struct PendingJsDialog {
    int browser_id;
    CefRefPtr<CefJSDialogCallback> callback;
};
struct PendingFileDialog {
    int browser_id;
    CefRefPtr<CefFileDialogCallback> callback;
};
struct PendingContextMenu {
    int browser_id;
    CefRefPtr<CefRunContextMenuCallback> callback;
    std::string link_url;
};
struct PendingDownloadStart {
    int browser_id;
    uint32_t download_id;
    CefRefPtr<CefBeforeDownloadCallback> callback;
};
// (browser_id, download_id) pairs the host cancelled via
// excef_resolve_download_starting with an empty path. There is no cancel
// on CefBeforeDownloadCallback — Continue("") means "download to the
// default temp dir", NOT cancel — so the actual cancellation happens in
// OnDownloadUpdated via CefDownloadItemCallback::Cancel().
std::mutex g_cancelled_downloads_mu;
std::set<std::pair<int, uint32_t>> g_cancelled_downloads;
struct PendingDownloadProgress {
    int browser_id;
    CefRefPtr<CefDownloadItemCallback> callback;
};
struct PendingAuth {
    int browser_id;
    CefRefPtr<CefAuthCallback> callback;
};
struct PendingPermissionPrompt {
    int browser_id;
    CefRefPtr<CefPermissionPromptCallback> callback;
};
struct PendingMediaAccess {
    int browser_id;
    CefRefPtr<CefMediaAccessCallback> callback;
    uint32_t requested;  // mask of requested perms, for clamping the response
};
struct PendingCertError {
    int browser_id;
    CefRefPtr<CefCallback> callback;
};
struct PendingJsInvoke {
    int browser_id;
    CefRefPtr<CefFrame> frame;
    int request_id;  // renderer-side promise id; round-tripped in the reply
};

// Scheme-request handler state. The resource handler instance lives across
// Open() / GetResponseHeaders() / Read() — the host's resolve fills in
// status_code / mime / body, then unblocks the CefCallback so CEF asks
// for headers + body.
class SchemeResourceHandler;
struct PendingSchemeRequest {
    int browser_id;
    CefRefPtr<SchemeResourceHandler> handler;
};
// Same shape as PendingSchemeRequest, separate type. The UrlResourceHandler
// class itself lives outside the anon namespace (see GetResourceHandler
// nearby) so Exclr8CefOsrHandler::GetResourceHandler can construct it;
// the forward decl + struct also live outside anon so the global pending
// map declared at exclr8cef:: scope can reference the type.

// Resource-request state. The CefRequest is kept alive until resolve
// so the host's new headers (if any) can be applied to it.
struct PendingResourceRequest {
    int browser_id;
    CefRefPtr<CefRequest> request;
    CefRefPtr<CefCallback> callback;
};
std::atomic<uint64_t> g_next_token{1};
std::mutex g_js_dialog_mu;
std::map<uint64_t, PendingJsDialog> g_js_dialog_pending;
std::mutex g_file_dialog_mu;
std::map<uint64_t, PendingFileDialog> g_file_dialog_pending;
std::mutex g_context_menu_mu;
std::map<uint64_t, PendingContextMenu> g_context_menu_pending;
std::mutex g_download_starting_mu;
std::map<uint64_t, PendingDownloadStart> g_download_starting_pending;
std::mutex g_download_progress_mu;
std::map<uint64_t, PendingDownloadProgress> g_download_progress_pending;
std::mutex g_auth_mu;
std::map<uint64_t, PendingAuth> g_auth_pending;
std::mutex g_permission_prompt_mu;
std::map<uint64_t, PendingPermissionPrompt> g_permission_prompt_pending;
std::mutex g_media_access_mu;
std::map<uint64_t, PendingMediaAccess> g_media_access_pending;
std::mutex g_cert_error_mu;
std::map<uint64_t, PendingCertError> g_cert_error_pending;
std::mutex g_js_invoke_mu;
std::map<uint64_t, PendingJsInvoke> g_js_invoke_pending;
std::mutex g_scheme_mu;
std::map<uint64_t, PendingSchemeRequest> g_scheme_pending;
std::mutex g_resource_request_mu;
std::map<uint64_t, PendingResourceRequest> g_resource_request_pending;

// Resource handler for our custom schemes. One instance per request.
// Open() defers via CefCallback while the host computes the response;
// the host's resolve fills body/status/mime and Continue()s the callback,
// at which point CEF calls GetResponseHeaders() then Read() repeatedly.
class SchemeResourceHandler : public CefResourceHandler {
public:
    SchemeResourceHandler(int browser_id, std::string url, std::string method)
        : browser_id_(browser_id),
          url_(std::move(url)),
          method_(std::move(method)),
          token_(g_next_token.fetch_add(1, std::memory_order_relaxed)) {}

    bool Open(CefRefPtr<CefRequest> /*request*/,
              bool& handle_request,
              CefRefPtr<CefCallback> callback) override {
        // Run host notification. If the host's handler resolves
        // synchronously (returns Continue/NotFound before unwinding), the
        // response is ready by the time we get back here — we mark
        // handle_request=true and don't need the deferred callback. If the
        // host is async (Resolve fires later), handle_request=false and
        // CEF waits for callback_->Continue(), which Resolve() invokes
        // once Open has returned.
        //
        // Open runs on the CEF IO thread; Resolve on the host thread. The
        // in_open_/resolved_/callback_ handshake is guarded by mu_ so an
        // async Resolve racing the tail of Open can't slip into the gap
        // where neither side fires the callback (request hangs) or fire it
        // while still inside Open (CEF → ERR_ABORTED).
        {
            std::lock_guard<std::mutex> lock(mu_);
            in_open_ = true;
            callback_ = callback;
        }
        if (!g_scheme_request_cb) {
            std::lock_guard<std::mutex> lock(mu_);
            status_code_ = 404; status_text_ = "Not Found";
            mime_type_ = "text/plain";
            resolved_ = true;
        } else {
            {
                std::lock_guard<std::mutex> lock(g_scheme_mu);
                g_scheme_pending[token_] = PendingSchemeRequest{browser_id_, this};
            }
            g_scheme_request_cb(browser_id_, token_, url_.c_str(), method_.c_str());
        }
        std::lock_guard<std::mutex> lock(mu_);
        in_open_ = false;
        if (resolved_) {
            // Sync path: no need for the callback (we'd call it from inside
            // Open which CEF treats as an error → net::ERR_ABORTED).
            callback_ = nullptr;
            handle_request = true;
            // The pending entry was consumed by the sync resolve; if it's
            // still registered (host never resolved → the 404 fallback
            // above), drop it so it can't be resolved twice.
            std::lock_guard<std::mutex> plock(g_scheme_mu);
            g_scheme_pending.erase(token_);
        } else {
            handle_request = false;
        }
        return true;
    }

    void GetResponseHeaders(CefRefPtr<CefResponse> response,
                            int64_t& response_length,
                            CefString& /*redirectUrl*/) override {
        response->SetStatus(status_code_);
        if (!status_text_.empty())
            response->SetStatusText(status_text_);
        if (!mime_type_.empty()) {
            // SetMimeType expects ONLY the type ("text/html"), not a full
            // Content-Type value with parameters ("text/html; charset=...").
            // CEF treats the full string as the type, fails its internal
            // is-this-HTML check, and the renderer falls back to
            // plain-text display. Strip everything after the semicolon
            // and pass the charset as a separate Content-Type header.
            auto semi = mime_type_.find(';');
            std::string type = semi == std::string::npos ? mime_type_
                                                          : mime_type_.substr(0, semi);
            response->SetMimeType(type);
            // Preserve the full Content-Type (with charset) in the header
            // map so the renderer can still pick up character encoding.
            CefResponse::HeaderMap headers;
            response->GetHeaderMap(headers);
            headers.emplace("Content-Type", mime_type_);
            response->SetHeaderMap(headers);
        }
        response_length = -1;
    }

    bool Read(void* data_out, int bytes_to_read, int& bytes_read,
              CefRefPtr<CefResourceReadCallback> /*callback*/) override {
        if (read_pos_ >= body_.size()) { bytes_read = 0; return false; }
        size_t avail = body_.size() - read_pos_;
        size_t copy = std::min<size_t>(avail, static_cast<size_t>(bytes_to_read));
        std::memcpy(data_out, body_.data() + read_pos_, copy);
        read_pos_ += copy;
        bytes_read = static_cast<int>(copy);
        return true;
    }

    void Cancel() override {
        {
            std::lock_guard<std::mutex> lock(g_scheme_mu);
            g_scheme_pending.erase(token_);
        }
        // Drop the deferred callback so a late host Resolve (already past
        // the pending-map lookup) can't Continue() a cancelled request.
        std::lock_guard<std::mutex> lock(mu_);
        callback_ = nullptr;
    }

    // Called by excef_resolve_scheme_request. Stash the response and unblock
    // the deferred Open() callback so CEF asks us for headers + body.
    void Resolve(int status_code, std::string status_text,
                 std::string mime_type,
                 std::vector<uint8_t> body) {
        CefRefPtr<CefCallback> cb;
        {
            std::lock_guard<std::mutex> lock(mu_);
            status_code_ = status_code;
            status_text_ = std::move(status_text);
            mime_type_ = std::move(mime_type);
            body_ = std::move(body);
            resolved_ = true;
            // Only Continue() if we're truly async (Open has already
            // returned). If we're still inside Open, the sync-path check in
            // Open will unblock CEF by setting handle_request=true.
            if (!in_open_ && callback_) {
                cb = callback_;
                callback_ = nullptr;
            }
        }
        if (cb) cb->Continue();
    }

    int browser_id() const { return browser_id_; }

private:
    int browser_id_;
    std::string url_;
    std::string method_;
    uint64_t token_;
    std::mutex mu_;  // guards the Open/Resolve handshake state below
    CefRefPtr<CefCallback> callback_;
    int status_code_ = 200;
    std::string status_text_ = "OK";
    std::string mime_type_ = "text/plain";
    std::vector<uint8_t> body_;
    size_t read_pos_ = 0;
    bool resolved_ = false;  // host called Resolve() yet?
    bool in_open_ = false;   // are we currently inside Open()?

    IMPLEMENT_REFCOUNTING(SchemeResourceHandler);
};

// Factory: one per scheme registered with CEF. Creates a fresh handler
// per request.
class SchemeFactory : public CefSchemeHandlerFactory {
public:
    CefRefPtr<CefResourceHandler> Create(CefRefPtr<CefBrowser> browser,
                                          CefRefPtr<CefFrame> /*frame*/,
                                          const CefString& /*scheme_name*/,
                                          CefRefPtr<CefRequest> request) override {
        // Runs on the CEF IO thread — hold the lock for the whole reverse
        // lookup so a concurrent create/close can't rebalance the map under
        // the iteration.
        int bid = 0;
        if (browser) {
            std::lock_guard<std::mutex> lock(g_osr_browsers_mu);
            for (const auto& [id, handler] : g_osr_browsers) {
                if (handler->browser() && handler->browser()->IsSame(browser)) {
                    bid = id;
                    break;
                }
            }
        }
        return new SchemeResourceHandler(bid,
                                          request->GetURL().ToString(),
                                          request->GetMethod().ToString());
    }
    IMPLEMENT_REFCOUNTING(SchemeFactory);
};

}  // namespace

// Streaming URL resource handler bookkeeping. Lives at exclr8cef:: scope
// (not anonymous) so the UrlResourceHandler class definition further down
// — also at exclr8cef:: scope — can reference these symbols. The class
// is forward-declared here and fully defined near GetResourceHandler.
//
// Lifecycle: GetResourceHandler() inserts a placeholder, then fires the
// host's should_handle callback. Inside that callback the host MAY call
// the resolve ABI synchronously — that's why we register the entry FIRST,
// so the lookup succeeds. If sync-resolved the placeholder.body /
// status_code / mime / status_text are filled in; Open() picks them up
// when CEF calls it. If async, the handler pointer is set during Open()
// and the eventual resolve will Continue() its callback.
class UrlResourceHandler;
struct PendingUrlHandler {
    int browser_id;
    CefRefPtr<UrlResourceHandler> handler;  // null until Open() registers
    bool resolved = false;                   // host has filled in fields below
    int status_code = 200;
    std::string status_text = "OK";
    std::string mime_type = "text/plain";
    std::vector<uint8_t> body;
    std::vector<std::pair<std::string, std::string>> extra_headers;
};
std::mutex g_url_handler_mu;
std::map<uint64_t, PendingUrlHandler> g_url_handler_pending;

Exclr8CefOsrHandler::Exclr8CefOsrHandler(int id, int width, int height,
                                          float device_scale_factor,
                                          excef_paint_callback_t paint_cb)
    : id_(id), width_(width), height_(height),
      device_scale_factor_(device_scale_factor > 0.0f ? device_scale_factor : 1.0f),
      paint_cb_(paint_cb) {}

bool Exclr8CefOsrHandler::OnProcessMessageReceived(
    CefRefPtr<CefBrowser> /*browser*/,
    CefRefPtr<CefFrame> frame,
    CefProcessId /*source_process*/,
    CefRefPtr<CefProcessMessage> message) {
    const std::string name = message->GetName().ToString();
    auto args = message->GetArgumentList();

    if (name == "EvalResult") {
        int request_id = args->GetInt(0);
        bool ok = args->GetBool(1);
        std::string payload = args->GetString(2).ToString();
        if (g_eval_result_cb) {
            g_eval_result_cb(id_, request_id, ok ? 1 : 0, payload.c_str());
        }
        return true;
    }

    if (name == "JsInvoke") {
        // Renderer JS bridge: window.exclr8cef.invoke(method, argsJson).
        // Message args: [request_id:int, method:string, argsJson:string].
        // We map the request_id to a token, store the frame so we can send
        // the reply back to the right renderer, and fire the host event.
        if (g_js_invoke_cb) {
            int request_id = args->GetInt(0);
            std::string method = args->GetString(1).ToString();
            std::string argsJson = args->GetString(2).ToString();
            uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
            {
                std::lock_guard<std::mutex> lock(g_js_invoke_mu);
                g_js_invoke_pending[token] = {id_, frame, request_id};
            }
            g_js_invoke_cb(id_, token, method.c_str(), argsJson.c_str());
        }
        return true;
    }

    return false;
}

void Exclr8CefOsrHandler::GetViewRect(CefRefPtr<CefBrowser> /*browser*/,
                                      CefRect& rect) {
    // View rect is in DIPs / CSS pixels; CEF multiplies by device_scale_factor
    // (returned from GetScreenInfo) to size the paint buffer.
    rect.x = 0;
    rect.y = 0;
    std::lock_guard<std::mutex> lock(size_mu_);
    rect.width = width_;
    rect.height = height_;
}

bool Exclr8CefOsrHandler::GetScreenInfo(CefRefPtr<CefBrowser> /*browser*/,
                                        CefScreenInfo& info) {
    info.device_scale_factor = device_scale_factor_;
    // Treat the entire view as the available screen — host doesn't carry
    // monitor-extent info across the C ABI in this version.
    std::lock_guard<std::mutex> lock(size_mu_);
    info.rect = CefRect(0, 0, width_, height_);
    info.available_rect = info.rect;
    return true;
}

void Exclr8CefOsrHandler::OnPaint(CefRefPtr<CefBrowser> /*browser*/,
                                  PaintElementType type,
                                  const RectList& /*dirtyRects*/,
                                  const void* buffer,
                                  int width, int height) {
    if (type == PET_VIEW) {
        if (paint_cb_) paint_cb_(id_, buffer, width, height);
    } else if (type == PET_POPUP) {
        if (g_popup_paint_cb) g_popup_paint_cb(id_, buffer, width, height);
    }
}

void Exclr8CefOsrHandler::OnPopupShow(CefRefPtr<CefBrowser> /*browser*/, bool show) {
    if (g_popup_show_cb) g_popup_show_cb(id_, show ? 1 : 0);
}

void Exclr8CefOsrHandler::OnPopupSize(CefRefPtr<CefBrowser> /*browser*/, const CefRect& rect) {
    if (g_popup_size_cb) g_popup_size_cb(id_, rect.x, rect.y, rect.width, rect.height);
}

void Exclr8CefOsrHandler::OnAcceleratedPaint(CefRefPtr<CefBrowser> /*browser*/,
                                              PaintElementType type,
                                              const RectList& /*dirtyRects*/,
                                              const CefAcceleratedPaintInfo& info) {
    if (!g_accelerated_paint_cb) return;
    // CefAcceleratedPaintInfo's shared-texture field name is
    // platform-specific (compile-time selected via cef_types_<plat>.h):
    //   macOS  : shared_texture_io_surface  (IOSurfaceRef)
    //   Windows: shared_texture_handle      (HANDLE — NT shared handle, D3D11)
    //   Linux  : no single handle — array of dma-buf `planes`. The C ABI
    //            takes a single void*, so we surface nullptr; a host that
    //            needs Linux GPU consumption has to extend the ABI to
    //            carry the plane array. For now the event still fires so
    //            timing / format / dims are usable.
#if defined(__APPLE__)
    const void* shared_handle = info.shared_texture_io_surface;
#elif defined(_WIN32)
    const void* shared_handle = info.shared_texture_handle;
#else
    const void* shared_handle = nullptr;
#endif
    if (type == PET_VIEW) {
        int expected_width;
        int expected_height;
        {
            std::lock_guard<std::mutex> lock(size_mu_);
            expected_width = static_cast<int>(
                std::lround(width_ * device_scale_factor_));
            expected_height = static_cast<int>(
                std::lround(height_ * device_scale_factor_));
        }
        constexpr int kPixelRoundingTolerance = 1;
        const bool current_size =
            std::abs(info.extra.coded_size.width - expected_width)
                <= kPixelRoundingTolerance
            && std::abs(info.extra.coded_size.height - expected_height)
                <= kPixelRoundingTolerance;
        if (!current_size) {
            // CEF can complete an old accelerated resize after the final
            // WasResized call. Treat the actual IOSurface dimensions as the
            // acknowledgement: keep reasserting the settled viewport at a
            // bounded 75 ms cadence until CEF produces the requested size.
            QueueSettledSizeOnUi();
        }
    }
    g_accelerated_paint_cb(id_,
                            static_cast<int>(type),
                            info.extra.coded_size.width,
                            info.extra.coded_size.height,
                            static_cast<int>(info.format),
                            info.extra.timestamp,
                            shared_handle);
}

void Exclr8CefOsrHandler::GetTouchHandleSize(CefRefPtr<CefBrowser> /*browser*/,
                                              cef_horizontal_alignment_t orientation,
                                              CefSize& size) {
    if (!g_touch_handle_size_cb) return;
    int w = 0, h = 0;
    g_touch_handle_size_cb(id_, static_cast<int>(orientation), &w, &h);
    if (w > 0) size.width = w;
    if (h > 0) size.height = h;
}

void Exclr8CefOsrHandler::OnTouchHandleStateChanged(CefRefPtr<CefBrowser> /*browser*/,
                                                     const CefTouchHandleState& state) {
    if (!g_touch_handle_state_cb) return;
    g_touch_handle_state_cb(id_,
                              state.touch_handle_id,
                              state.flags,
                              state.enabled,
                              static_cast<int>(state.orientation),
                              state.mirror_vertical,
                              state.mirror_horizontal,
                              state.origin.x,
                              state.origin.y,
                              state.alpha);
}

void Exclr8CefOsrHandler::OnImeCompositionRangeChanged(CefRefPtr<CefBrowser> /*browser*/,
                                                        const CefRange& selected_range,
                                                        const RectList& character_bounds) {
    if (!g_ime_composition_range_cb) return;
    // Flatten character_bounds into a contiguous int array (4 ints per rect:
    // x, y, width, height). Stack-allocated for typical small counts; the
    // host copies what it needs inside the callback.
    std::vector<int> flat;
    flat.reserve(character_bounds.size() * 4);
    for (const auto& r : character_bounds) {
        flat.push_back(r.x);
        flat.push_back(r.y);
        flat.push_back(r.width);
        flat.push_back(r.height);
    }
    g_ime_composition_range_cb(id_,
                                 selected_range.from, selected_range.to,
                                 static_cast<int>(character_bounds.size()),
                                 flat.empty() ? nullptr : flat.data());
}

void Exclr8CefOsrHandler::OnVirtualKeyboardRequested(CefRefPtr<CefBrowser> /*browser*/,
                                                      TextInputMode input_mode) {
    if (g_virtual_keyboard_cb) g_virtual_keyboard_cb(id_, static_cast<int>(input_mode));
}

void Exclr8CefOsrHandler::OnAccessibilityTreeChange(CefRefPtr<CefValue> value) {
    if (!g_a11y_tree_cb) return;
    auto json = CefWriteJSON(value, JSON_WRITER_DEFAULT);
    std::string s = json.ToString();
    g_a11y_tree_cb(id_, s.c_str());
}

void Exclr8CefOsrHandler::OnAccessibilityLocationChange(CefRefPtr<CefValue> value) {
    if (!g_a11y_location_cb) return;
    auto json = CefWriteJSON(value, JSON_WRITER_DEFAULT);
    std::string s = json.ToString();
    g_a11y_location_cb(id_, s.c_str());
}

void Exclr8CefOsrHandler::OnScrollOffsetChanged(CefRefPtr<CefBrowser> /*browser*/,
                                                 double x, double y) {
    if (g_scroll_offset_cb) g_scroll_offset_cb(id_, x, y);
}

bool Exclr8CefOsrHandler::OnAutoResize(CefRefPtr<CefBrowser> /*browser*/,
                                        const CefSize& new_size) {
    if (g_auto_resize_cb) g_auto_resize_cb(id_, new_size.width, new_size.height);
    // Return true to mark the resize as handled (the host responds via its
    // event hook). Returning false would suggest Chromium take its own
    // action, which in OSR mode is a no-op anyway.
    return true;
}

bool Exclr8CefOsrHandler::OnJSDialog(CefRefPtr<CefBrowser> /*browser*/,
                                      const CefString& /*origin_url*/,
                                      JSDialogType dialog_type,
                                      const CefString& message_text,
                                      const CefString& default_prompt_text,
                                      CefRefPtr<CefJSDialogCallback> callback,
                                      bool& /*suppress_message*/) {
    if (!g_js_dialog_cb) return false;  // fall back to CEF default
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        g_js_dialog_pending[token] = PendingJsDialog{id_, callback};
    }
    std::string msg = message_text.ToString();
    std::string def = default_prompt_text.ToString();
    g_js_dialog_cb(id_, token, static_cast<int>(dialog_type),
                   msg.c_str(), def.c_str());
    return true;  // we'll respond asynchronously via excef_resolve_js_dialog
}

bool Exclr8CefOsrHandler::OnBeforeUnloadDialog(CefRefPtr<CefBrowser> /*browser*/,
                                                const CefString& message_text,
                                                bool /*is_reload*/,
                                                CefRefPtr<CefJSDialogCallback> callback) {
    if (!g_js_dialog_cb) return false;
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        g_js_dialog_pending[token] = PendingJsDialog{id_, callback};
    }
    std::string msg = message_text.ToString();
    g_js_dialog_cb(id_, token, /*dialog_type=*/3, msg.c_str(), "");
    return true;
}

bool Exclr8CefOsrHandler::GetAuthCredentials(CefRefPtr<CefBrowser> /*browser*/,
                                              const CefString& /*origin_url*/,
                                              bool isProxy,
                                              const CefString& host,
                                              int port,
                                              const CefString& realm,
                                              const CefString& scheme,
                                              CefRefPtr<CefAuthCallback> callback) {
    if (!g_auth_request_cb) return false;  // fall back: CEF cancels the request
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_auth_mu);
        g_auth_pending[token] = PendingAuth{id_, callback};
    }
    std::string host_s = host.ToString();
    std::string realm_s = realm.ToString();
    std::string scheme_s = scheme.ToString();
    g_auth_request_cb(id_, token, isProxy ? 1 : 0,
                      host_s.c_str(), port,
                      realm_s.c_str(), scheme_s.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnRequestMediaAccessPermission(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        const CefString& requesting_origin,
        uint32_t requested_permissions,
        CefRefPtr<CefMediaAccessCallback> callback) {
    if (!g_media_access_cb) return false;  // default-deny with Alloy style
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_media_access_mu);
        g_media_access_pending[token] = PendingMediaAccess{
            id_, callback, requested_permissions};
    }
    std::string origin_s = requesting_origin.ToString();
    g_media_access_cb(id_, token, origin_s.c_str(),
                       static_cast<int>(requested_permissions));
    return true;
}

bool Exclr8CefOsrHandler::OnShowPermissionPrompt(
        CefRefPtr<CefBrowser> /*browser*/,
        uint64_t prompt_id,
        const CefString& requesting_origin,
        uint32_t requested_permissions,
        CefRefPtr<CefPermissionPromptCallback> callback) {
    if (!g_permission_prompt_cb) return false;  // default-deny
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_permission_prompt_mu);
        g_permission_prompt_pending[token] = PendingPermissionPrompt{
            id_, callback};
    }
    std::string origin_s = requesting_origin.ToString();
    g_permission_prompt_cb(id_, token, prompt_id, origin_s.c_str(),
                            static_cast<int>(requested_permissions));
    return true;
}

void Exclr8CefOsrHandler::OnRenderProcessTerminated(CefRefPtr<CefBrowser> /*browser*/,
                                                     TerminationStatus status,
                                                     int error_code,
                                                     const CefString& error_string) {
    if (g_render_process_gone_cb) {
        std::string err = error_string.ToString();
        g_render_process_gone_cb(id_, static_cast<int>(status),
                                  error_code, err.c_str());
    }
}

// ---- CefFocusHandler ------------------------------------------------------

void Exclr8CefOsrHandler::OnTakeFocus(CefRefPtr<CefBrowser> /*browser*/, bool next) {
    if (g_take_focus_cb) g_take_focus_cb(id_, next ? 1 : 0);
}

bool Exclr8CefOsrHandler::OnSetFocus(CefRefPtr<CefBrowser> /*browser*/, FocusSource source) {
    if (!g_set_focus_cb) return false;
    return g_set_focus_cb(id_, static_cast<int>(source)) != 0;
}

void Exclr8CefOsrHandler::OnGotFocus(CefRefPtr<CefBrowser> /*browser*/) {
    if (g_got_focus_cb) g_got_focus_cb(id_);
}

// ---- CefKeyboardHandler ---------------------------------------------------

bool Exclr8CefOsrHandler::OnPreKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                                          const CefKeyEvent& event,
                                          CefEventHandle /*os_event*/,
                                          bool* /*is_keyboard_shortcut*/) {
    if (!g_pre_key_cb) return false;
    return g_pre_key_cb(id_, static_cast<int>(event.type),
                        static_cast<int>(event.modifiers),
                        event.windows_key_code, event.native_key_code,
                        event.is_system_key ? 1 : 0) != 0;
}

bool Exclr8CefOsrHandler::OnKeyEvent(CefRefPtr<CefBrowser> /*browser*/,
                                       const CefKeyEvent& event,
                                       CefEventHandle /*os_event*/) {
    if (!g_key_event_cb) return false;
    return g_key_event_cb(id_, static_cast<int>(event.type),
                           static_cast<int>(event.modifiers),
                           event.windows_key_code, event.native_key_code,
                           event.is_system_key ? 1 : 0) != 0;
}

// ---- CefFrameHandler ------------------------------------------------------

namespace {
// Pull the fields we hand to the host out of a frame. Frame methods are
// safe to call from the UI thread; the callback is invoked synchronously.
void EmitFrame(int browser_id, int event_type, bool reattached_unused,
                CefRefPtr<CefFrame> frame) {
    (void)reattached_unused;
    if (!g_frame_lifecycle_cb || !frame) return;
    std::string fid = frame->GetIdentifier().ToString();
    std::string name = frame->GetName().ToString();
    std::string url = frame->GetURL().ToString();
    std::string parent;
    if (auto p = frame->GetParent()) parent = p->GetIdentifier().ToString();
    g_frame_lifecycle_cb(browser_id, event_type,
                          fid.c_str(), parent.c_str(),
                          name.c_str(), url.c_str(),
                          frame->IsMain() ? 1 : 0);
}
}  // namespace

void Exclr8CefOsrHandler::OnFrameCreated(CefRefPtr<CefBrowser> /*browser*/,
                                          CefRefPtr<CefFrame> frame) {
    EmitFrame(id_, 0, false, frame);
}

void Exclr8CefOsrHandler::OnFrameAttached(CefRefPtr<CefBrowser> /*browser*/,
                                           CefRefPtr<CefFrame> frame,
                                           bool /*reattached*/) {
    EmitFrame(id_, 1, false, frame);
}

void Exclr8CefOsrHandler::OnFrameDetached(CefRefPtr<CefBrowser> /*browser*/,
                                           CefRefPtr<CefFrame> frame) {
    EmitFrame(id_, 2, false, frame);
}

void Exclr8CefOsrHandler::OnMainFrameChanged(CefRefPtr<CefBrowser> /*browser*/,
                                              CefRefPtr<CefFrame> old_frame,
                                              CefRefPtr<CefFrame> new_frame) {
    if (!g_main_frame_changed_cb) return;
    std::string oldId, newId;
    if (old_frame) oldId = old_frame->GetIdentifier().ToString();
    if (new_frame) newId = new_frame->GetIdentifier().ToString();
    g_main_frame_changed_cb(id_, oldId.c_str(), newId.c_str());
}

// ---- CefAudioHandler ------------------------------------------------------

CefRefPtr<CefAudioHandler> Exclr8CefOsrHandler::GetAudioHandler() {
    std::lock_guard<std::mutex> lock(g_audio_enabled_mu);
    auto it = g_audio_enabled.find(id_);
    return (it != g_audio_enabled.end() && it->second) ? this : nullptr;
}

bool Exclr8CefOsrHandler::GetAudioParameters(CefRefPtr<CefBrowser> /*browser*/,
                                              CefAudioParameters& /*params*/) {
    // Accept whatever CEF picked (default 48kHz stereo). Returning false
    // here would block audio entirely; returning true lets CEF proceed.
    return true;
}

void Exclr8CefOsrHandler::OnAudioStreamStarted(CefRefPtr<CefBrowser> /*browser*/,
                                                const CefAudioParameters& params,
                                                int channels) {
    // Cache the channel count for OnAudioStreamPacket — CEF's packet
    // callback doesn't carry it, and its data array has exactly this many
    // entries with NO null terminator, so it cannot be derived by scanning.
    audio_channels_ = channels;
    if (g_audio_started_cb) {
        g_audio_started_cb(id_,
                            static_cast<int>(params.channel_layout),
                            params.sample_rate,
                            params.frames_per_buffer,
                            channels);
    }
}

void Exclr8CefOsrHandler::OnAudioStreamPacket(CefRefPtr<CefBrowser> /*browser*/,
                                                const float** data,
                                                int frames,
                                                int64_t pts) {
    if (!g_audio_packet_cb || frames <= 0 || !data) return;
    // CEF gives us planar PCM (data[c][f]). Interleave to data[f*C+c] so
    // the host doesn't have to walk channel pointers across the FFI. The
    // channel count comes from OnAudioStreamStarted (cached above) — the
    // data array is exactly that long, so scanning for a null sentinel
    // would read past its end.
    int channels = audio_channels_;
    if (channels <= 0) return;
    std::vector<float> interleaved(static_cast<size_t>(frames) * channels);
    for (int f = 0; f < frames; ++f) {
        for (int c = 0; c < channels; ++c) {
            interleaved[f * channels + c] = data[c][f];
        }
    }
    g_audio_packet_cb(id_, interleaved.data(), frames, channels, pts);
}

void Exclr8CefOsrHandler::OnAudioStreamStopped(CefRefPtr<CefBrowser> /*browser*/) {
    if (g_audio_stopped_cb) g_audio_stopped_cb(id_);
}

void Exclr8CefOsrHandler::OnAudioStreamError(CefRefPtr<CefBrowser> /*browser*/,
                                              const CefString& message) {
    if (g_audio_error_cb) {
        std::string m = message.ToString();
        g_audio_error_cb(id_, m.c_str());
    }
}

// ---- CefCommandHandler ----------------------------------------------------

bool Exclr8CefOsrHandler::OnChromeCommand(CefRefPtr<CefBrowser> /*browser*/,
                                            int command_id,
                                            cef_window_open_disposition_t disposition) {
    if (!g_chrome_command_cb) return false;
    return g_chrome_command_cb(id_, command_id, static_cast<int>(disposition)) != 0;
}

bool Exclr8CefOsrHandler::IsChromeAppMenuItemVisible(CefRefPtr<CefBrowser> /*browser*/,
                                                      int command_id) {
    if (!g_app_menu_visible_cb) return true;
    return g_app_menu_visible_cb(id_, command_id) != 0;
}

bool Exclr8CefOsrHandler::IsChromeAppMenuItemEnabled(CefRefPtr<CefBrowser> /*browser*/,
                                                      int command_id) {
    if (!g_app_menu_enabled_cb) return true;
    return g_app_menu_enabled_cb(id_, command_id) != 0;
}

bool Exclr8CefOsrHandler::IsChromePageActionIconVisible(cef_chrome_page_action_icon_type_t icon_type) {
    if (!g_page_action_visible_cb) return true;
    return g_page_action_visible_cb(static_cast<int>(icon_type)) != 0;
}

bool Exclr8CefOsrHandler::IsChromeToolbarButtonVisible(cef_chrome_toolbar_button_type_t button_type) {
    if (!g_toolbar_button_visible_cb) return true;
    return g_toolbar_button_visible_cb(static_cast<int>(button_type)) != 0;
}

bool Exclr8CefOsrHandler::OnCertificateError(
        CefRefPtr<CefBrowser> /*browser*/,
        cef_errorcode_t cert_error,
        const CefString& request_url,
        CefRefPtr<CefSSLInfo> ssl_info,
        CefRefPtr<CefCallback> callback) {
    if (!g_cert_error_cb) return false;  // CEF reports as load failure
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_cert_error_mu);
        g_cert_error_pending[token] = PendingCertError{id_, callback};
    }
    std::string url = request_url.ToString();
    std::string subject_cn, issuer_cn;
    if (ssl_info) {
        if (auto cert = ssl_info->GetX509Certificate()) {
            if (auto subject = cert->GetSubject())
                subject_cn = subject->GetCommonName().ToString();
            if (auto issuer = cert->GetIssuer())
                issuer_cn = issuer->GetCommonName().ToString();
        }
    }
    g_cert_error_cb(id_, token, static_cast<int>(cert_error),
                     url.c_str(), subject_cn.c_str(), issuer_cn.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnBeforeBrowse(
    CefRefPtr<CefBrowser> /*browser*/,
    CefRefPtr<CefFrame> frame,
    CefRefPtr<CefRequest> request,
    bool user_gesture,
    bool is_redirect) {
    // This ABI is deliberately a top-level origin/navigation gate. Subresource
    // and iframe policy remains available through ResourceRequest.
    if (!frame || !frame->IsMain() || !g_before_browse_cb) return false;

    std::string url = request ? request->GetURL().ToString() : std::string();
    return g_before_browse_cb(id_, url.c_str(),
                              user_gesture ? 1 : 0,
                              is_redirect ? 1 : 0) != 0;
}

CefRefPtr<CefResourceRequestHandler>
Exclr8CefOsrHandler::GetResourceRequestHandler(
    CefRefPtr<CefBrowser> /*browser*/,
    CefRefPtr<CefFrame> /*frame*/,
    CefRefPtr<CefRequest> /*request*/,
    bool /*is_navigation*/,
    bool /*is_download*/,
    const CefString& /*request_initiator*/,
    bool& /*disable_default_handling*/) {
    // Reuse this handler instance — we only override OnBeforeResourceLoad
    // and that method works fine for every request from this browser.
    // Hosts that don't subscribe to the resource-request event get a
    // free no-op (OnBeforeResourceLoad returns RV_CONTINUE).
    if (!g_resource_request_cb) return nullptr;  // skip overhead entirely
    return this;
}

// Header serialization helpers — in namespace exclr8cef (not the anon
// namespace) so the extern "C" excef_resolve_resource_request below can
// reach them via the namespace qualifier.
std::string SerializeHeaders(const CefRequest::HeaderMap& headers) {
    std::string out;
    for (const auto& kv : headers) {
        out += kv.first.ToString();
        out += ": ";
        out += kv.second.ToString();
        out += '\n';
    }
    if (!out.empty()) out.pop_back();  // drop trailing newline
    return out;
}

void ParseHeaders(const char* s, CefRequest::HeaderMap& out) {
    if (!s || !*s) return;
    std::string str(s);
    size_t start = 0;
    for (size_t i = 0; i <= str.size(); ++i) {
        if (i == str.size() || str[i] == '\n') {
            if (i > start) {
                auto line = str.substr(start, i - start);
                auto colon = line.find(':');
                if (colon != std::string::npos) {
                    std::string name = line.substr(0, colon);
                    std::string value = line.substr(colon + 1);
                    if (!value.empty() && value.front() == ' ') value.erase(0, 1);
                    out.emplace(CefString(name), CefString(value));
                }
            }
            start = i + 1;
        }
    }
}

cef_return_value_t Exclr8CefOsrHandler::OnBeforeResourceLoad(
    CefRefPtr<CefBrowser> /*browser*/,
    CefRefPtr<CefFrame> /*frame*/,
    CefRefPtr<CefRequest> request,
    CefRefPtr<CefCallback> callback) {
    if (!g_resource_request_cb) return RV_CONTINUE;

    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_resource_request_mu);
        g_resource_request_pending[token] = PendingResourceRequest{id_, request, callback};
    }
    std::string url = request->GetURL().ToString();
    std::string method = request->GetMethod().ToString();
    CefRequest::HeaderMap headers;
    request->GetHeaderMap(headers);
    std::string serialized = SerializeHeaders(headers);
    int rtype = static_cast<int>(request->GetResourceType());
    g_resource_request_cb(id_, token, url.c_str(), method.c_str(),
                          rtype, serialized.c_str());
    return RV_CONTINUE_ASYNC;
}

// ---- CefResponseFilter ----------------------------------------------------
//
// Bridges CEF's Filter() chunked API to the host's sync callback. Each
// instance carries the browser id + a token the host can use to keep
// per-response state across chunks (the same token is passed back on
// every Filter() call and on the finalize callback at end-of-life).
class Exclr8ResponseFilter : public CefResponseFilter {
public:
    Exclr8ResponseFilter(int browser_id, uint64_t token)
        : browser_id_(browser_id), token_(token) {}

    ~Exclr8ResponseFilter() override {
        if (g_response_filter_finalize_cb) {
            g_response_filter_finalize_cb(browser_id_, token_);
        }
    }

    bool InitFilter() override { return true; }

    FilterStatus Filter(void* data_in, size_t data_in_size, size_t& data_in_read,
                         void* data_out, size_t data_out_size, size_t& data_out_written) override {
        data_in_read = 0;
        data_out_written = 0;
        if (!g_response_filter_cb) {
            // No filter callback installed — treat as identity: copy as
            // many bytes as fit, signal DONE when input is exhausted.
            size_t n = std::min(data_in_size, data_out_size);
            if (data_in && data_out && n > 0) {
                std::memcpy(data_out, data_in, n);
            }
            data_in_read = n;
            data_out_written = n;
            return (data_in_size == 0)
                ? RESPONSE_FILTER_DONE
                : RESPONSE_FILTER_NEED_MORE_DATA;
        }

        int bytes_read = 0, bytes_written = 0;
        // Clamp sizes to int range — CEF chunks are KBs in practice.
        int in_n = data_in_size > static_cast<size_t>(INT_MAX)
                    ? INT_MAX : static_cast<int>(data_in_size);
        int out_n = data_out_size > static_cast<size_t>(INT_MAX)
                     ? INT_MAX : static_cast<int>(data_out_size);
        int status = g_response_filter_cb(browser_id_, token_,
                                            static_cast<const unsigned char*>(data_in), in_n,
                                            static_cast<unsigned char*>(data_out), out_n,
                                            &bytes_read, &bytes_written);
        if (bytes_read < 0) bytes_read = 0;
        if (bytes_written < 0) bytes_written = 0;
        data_in_read = static_cast<size_t>(bytes_read);
        data_out_written = static_cast<size_t>(bytes_written);
        switch (status) {
            case 1:  return RESPONSE_FILTER_DONE;
            case -1: return RESPONSE_FILTER_ERROR;
            default: return RESPONSE_FILTER_NEED_MORE_DATA;
        }
    }

private:
    int browser_id_;
    uint64_t token_;
    IMPLEMENT_REFCOUNTING(Exclr8ResponseFilter);
};

// ---- Streaming resource handler -----------------------------------------
//
// Same deferred-resolve pattern as SchemeResourceHandler but driven by
// `g_should_handle_resource_cb` (per-request URL claim) instead of the
// scheme-factory path. Reuses the shared `g_scheme_pending` map so the
// host calls the same `excef_resolve_resource_handler_request` ABI to
// fill in the response — no separate token pool.
class UrlResourceHandler : public CefResourceHandler {
public:
    UrlResourceHandler(int browser_id, uint64_t token,
                        std::string url, std::string method)
        : browser_id_(browser_id), url_(std::move(url)),
          method_(std::move(method)), token_(token) {}

    bool Open(CefRefPtr<CefRequest> /*request*/,
              bool& handle_request,
              CefRefPtr<CefCallback> callback) override {
        // Same locked handshake as SchemeResourceHandler::Open — see the
        // comment there. Open runs on the CEF IO thread, Resolve on the
        // host thread.
        {
            std::lock_guard<std::mutex> lock(mu_);
            in_open_ = true;
            callback_ = callback;
        }
        bool sync_resolved = false;
        {
            std::lock_guard<std::mutex> lock(g_url_handler_mu);
            auto it = g_url_handler_pending.find(token_);
            if (it != g_url_handler_pending.end()) {
                // Sync-resolved path: host called resolve from inside the
                // should_handle callback before we ever got here. The
                // entry is fully consumed — erase it so it can't be
                // resolved a second time and doesn't outlive the request
                // (it was previously kept "for Cancel()", but Cancel never
                // actually erased from this map — entries leaked).
                if (it->second.resolved) {
                    status_code_ = it->second.status_code;
                    status_text_ = std::move(it->second.status_text);
                    mime_type_ = std::move(it->second.mime_type);
                    body_ = std::move(it->second.body);
                    extra_headers_ = std::move(it->second.extra_headers);
                    sync_resolved = true;
                    g_url_handler_pending.erase(it);
                } else {
                    // Wire up the handler pointer so a still-pending async
                    // resolve can find us.
                    it->second.handler = this;
                }
            } else {
                g_url_handler_pending[token_] = PendingUrlHandler{browser_id_, this};
            }
        }
        std::lock_guard<std::mutex> lock(mu_);
        in_open_ = false;
        if (sync_resolved) resolved_ = true;
        if (resolved_) {
            // Sync path — don't Continue() the callback (calling it from
            // inside Open is treated as net::ERR_ABORTED by CEF).
            callback_ = nullptr;
            handle_request = true;
        } else {
            handle_request = false;
        }
        return true;
    }

    void GetResponseHeaders(CefRefPtr<CefResponse> response,
                             int64_t& response_length,
                             CefString& /*redirectUrl*/) override {
        response->SetStatus(status_code_);
        if (!status_text_.empty()) response->SetStatusText(status_text_);
        if (!mime_type_.empty()) {
            auto semi = mime_type_.find(';');
            std::string type = semi == std::string::npos ? mime_type_ : mime_type_.substr(0, semi);
            response->SetMimeType(type);
            CefResponse::HeaderMap headers;
            response->GetHeaderMap(headers);
            headers.emplace("Content-Type", mime_type_);
            for (const auto& [k, v] : extra_headers_) {
                headers.emplace(k, v);
            }
            response->SetHeaderMap(headers);
        }
        response_length = -1;
    }

    bool Read(void* data_out, int bytes_to_read, int& bytes_read,
               CefRefPtr<CefResourceReadCallback> /*callback*/) override {
        if (read_pos_ >= body_.size()) { bytes_read = 0; return false; }
        size_t avail = body_.size() - read_pos_;
        size_t copy = std::min<size_t>(avail, static_cast<size_t>(bytes_to_read));
        std::memcpy(data_out, body_.data() + read_pos_, copy);
        read_pos_ += copy;
        bytes_read = static_cast<int>(copy);
        return true;
    }

    void Cancel() override {
        // This handler's bookkeeping lives in g_url_handler_pending (NOT
        // g_scheme_pending — a previous version erased from the wrong map,
        // leaking the entry and letting a late host resolve Continue() a
        // request CEF had already cancelled).
        {
            std::lock_guard<std::mutex> lock(g_url_handler_mu);
            g_url_handler_pending.erase(token_);
        }
        std::lock_guard<std::mutex> lock(mu_);
        callback_ = nullptr;
    }

    // Host calls excef_resolve_resource_handler_request → dispatch here.
    void Resolve(int status_code, std::string status_text,
                  std::string mime_type, std::vector<uint8_t> body,
                  std::vector<std::pair<std::string, std::string>> headers) {
        CefRefPtr<CefCallback> cb;
        {
            std::lock_guard<std::mutex> lock(mu_);
            status_code_ = status_code;
            status_text_ = std::move(status_text);
            mime_type_ = std::move(mime_type);
            body_ = std::move(body);
            extra_headers_ = std::move(headers);
            resolved_ = true;
            if (!in_open_ && callback_) {
                cb = callback_;
                callback_ = nullptr;
            }
        }
        if (cb) cb->Continue();
    }

private:
    int browser_id_;
    std::string url_;
    std::string method_;
    uint64_t token_;
    std::mutex mu_;  // guards the Open/Resolve handshake state below
    CefRefPtr<CefCallback> callback_;
    int status_code_ = 200;
    std::string status_text_ = "OK";
    std::string mime_type_ = "text/plain";
    std::vector<uint8_t> body_;
    std::vector<std::pair<std::string, std::string>> extra_headers_;
    size_t read_pos_ = 0;
    bool resolved_ = false;
    bool in_open_ = false;
    IMPLEMENT_REFCOUNTING(UrlResourceHandler);
};

// ---- LoadString large-document transport ----------------------------------
//
// Chromium hard-caps URLs at url::kMaxURLChars (2 MB) and silently drops
// longer navigations at the IPC boundary — no LoadStart/LoadEnd/LoadError
// ever fires. So HTML that would blow the cap as a base64 data: URL is
// instead stashed here (one document per browser, replaced on each
// oversized LoadString) and served through GetResourceHandler under a
// synthetic https URL. `.internal` is a reserved TLD: it can never
// resolve, and the request never reaches the network because the handler
// claims it first. Reloads keep working because the document stays
// registered until the next LoadString or browser close.
constexpr const char kLoadStringUrlPrefix[] =
    "https://loadstring.exclr8cef.internal/";
struct PendingLoadStringDoc {
    uint64_t token;    // distinguishes stale reloads from the current doc
    std::string html;
};
std::mutex g_loadstring_mu;
std::map<int, PendingLoadStringDoc> g_loadstring_docs;

namespace {

// Fully-synchronous in-memory resource handler: the body is known at
// construction, so Open() completes immediately (no deferred callback).
class StringResourceHandler : public CefResourceHandler {
public:
    StringResourceHandler(std::string body, int status, std::string content_type)
        : body_(std::move(body)), status_(status),
          content_type_(std::move(content_type)) {}

    bool Open(CefRefPtr<CefRequest> /*request*/,
              bool& handle_request,
              CefRefPtr<CefCallback> /*callback*/) override {
        handle_request = true;
        return true;
    }

    void GetResponseHeaders(CefRefPtr<CefResponse> response,
                            int64_t& response_length,
                            CefString& /*redirectUrl*/) override {
        response->SetStatus(status_);
        // SetMimeType wants the bare type; keep the full Content-Type
        // (with charset) in the header map — same split as the scheme
        // handlers above.
        auto semi = content_type_.find(';');
        response->SetMimeType(semi == std::string::npos
                                  ? content_type_
                                  : content_type_.substr(0, semi));
        CefResponse::HeaderMap headers;
        response->GetHeaderMap(headers);
        headers.emplace("Content-Type", content_type_);
        response->SetHeaderMap(headers);
        response_length = static_cast<int64_t>(body_.size());
    }

    bool Read(void* data_out, int bytes_to_read, int& bytes_read,
              CefRefPtr<CefResourceReadCallback> /*callback*/) override {
        if (read_pos_ >= body_.size()) { bytes_read = 0; return false; }
        size_t n = std::min<size_t>(body_.size() - read_pos_,
                                    static_cast<size_t>(bytes_to_read));
        std::memcpy(data_out, body_.data() + read_pos_, n);
        read_pos_ += n;
        bytes_read = static_cast<int>(n);
        return true;
    }

    void Cancel() override {}

private:
    std::string body_;
    int status_;
    std::string content_type_;
    size_t read_pos_ = 0;
    IMPLEMENT_REFCOUNTING(StringResourceHandler);
};

}  // namespace

CefRefPtr<CefResourceHandler> Exclr8CefOsrHandler::GetResourceHandler(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        CefRefPtr<CefRequest> request) {
    if (!request) return nullptr;

    // Internal LoadString transport — checked before the host callback so
    // hosts can't accidentally shadow it. Runs on the CEF IO thread.
    {
        std::string req_url = request->GetURL().ToString();
        if (req_url.compare(0, sizeof(kLoadStringUrlPrefix) - 1,
                            kLoadStringUrlPrefix) == 0) {
            std::lock_guard<std::mutex> lock(g_loadstring_mu);
            auto it = g_loadstring_docs.find(id_);
            if (it != g_loadstring_docs.end() &&
                req_url == kLoadStringUrlPrefix + std::to_string(it->second.token)) {
                return new StringResourceHandler(
                    it->second.html, 200, "text/html; charset=utf-8");
            }
            // Anything else on the internal host (stale token after a newer
            // LoadString, favicon probe) → 404, never the network.
            return new StringResourceHandler("", 404, "text/plain");
        }
    }

    if (!g_should_handle_resource_cb) return nullptr;
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    std::string url = request->GetURL().ToString();
    std::string method = request->GetMethod().ToString();
    // Pre-register the placeholder so the host can resolve synchronously
    // from inside the should_handle callback. If it claims (returns 1) we
    // keep the entry; if it skips (returns 0) we erase it.
    {
        std::lock_guard<std::mutex> lock(g_url_handler_mu);
        g_url_handler_pending[token] = PendingUrlHandler{id_, nullptr};
    }
    int claim = g_should_handle_resource_cb(id_, token, url.c_str(), method.c_str());
    if (claim == 0) {
        std::lock_guard<std::mutex> lock(g_url_handler_mu);
        g_url_handler_pending.erase(token);
        return nullptr;
    }
    return new UrlResourceHandler(id_, token, std::move(url), std::move(method));
}

CefRefPtr<CefResponseFilter> Exclr8CefOsrHandler::GetResourceResponseFilter(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        CefRefPtr<CefRequest> request,
        CefRefPtr<CefResponse> response) {
    if (!g_should_filter_cb) return nullptr;
    std::string url = request ? request->GetURL().ToString() : std::string{};
    int status = response ? response->GetStatus() : 0;
    std::string mime = response ? response->GetMimeType().ToString() : std::string{};
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    int want = g_should_filter_cb(id_, token, url.c_str(), status, mime.c_str());
    if (want == 0) return nullptr;
    return new Exclr8ResponseFilter(id_, token);
}

void Exclr8CefOsrHandler::OnFindResult(CefRefPtr<CefBrowser> /*browser*/,
                                        int identifier,
                                        int count,
                                        const CefRect& /*selectionRect*/,
                                        int activeMatchOrdinal,
                                        bool finalUpdate) {
    if (g_find_result_cb) {
        g_find_result_cb(id_, identifier, count, activeMatchOrdinal,
                         finalUpdate ? 1 : 0);
    }
}

bool Exclr8CefOsrHandler::OnBeforeDownload(CefRefPtr<CefBrowser> /*browser*/,
                                            CefRefPtr<CefDownloadItem> item,
                                            const CefString& suggested_name,
                                            CefRefPtr<CefBeforeDownloadCallback> callback) {
    if (!g_download_starting_cb) {
        // Without a host subscriber, just let CEF use the default save path.
        // (No-op here; CEF's default handler will not fire because we
        // installed ourselves, so we must give it a path or it'll hang.
        // Pick Downloads/<suggested>.
        std::string fallback = suggested_name.ToString();
        if (fallback.empty()) fallback = "download.bin";
        callback->Continue(fallback, /*show_dialog=*/true);
        return true;
    }
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_download_starting_mu);
        g_download_starting_pending[token] = PendingDownloadStart{id_, item->GetId(), callback};
    }
    std::string url = item->GetURL().ToString();
    std::string name = suggested_name.ToString();
    std::string mime = item->GetMimeType().ToString();
    g_download_starting_cb(id_, token, item->GetId(),
                           url.c_str(), name.c_str(), mime.c_str(),
                           item->GetTotalBytes());
    return true;
}

void Exclr8CefOsrHandler::OnDownloadUpdated(CefRefPtr<CefBrowser> /*browser*/,
                                             CefRefPtr<CefDownloadItem> item,
                                             CefRefPtr<CefDownloadItemCallback> callback) {
    // Host-cancelled download (empty path in excef_resolve_download_starting)?
    // This is where the cancellation actually executes — see the comment on
    // g_cancelled_downloads.
    {
        std::lock_guard<std::mutex> lock(g_cancelled_downloads_mu);
        auto key = std::make_pair(id_, item->GetId());
        if (g_cancelled_downloads.count(key)) {
            if (item->IsInProgress()) {
                if (callback) callback->Cancel();
            } else {
                g_cancelled_downloads.erase(key);  // terminal — done tracking
            }
            return;  // don't surface progress for a cancelled download
        }
    }
    if (!g_download_progress_cb) return;
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_download_progress_mu);
        g_download_progress_pending[token] = PendingDownloadProgress{id_, callback};
    }
    int state = item->IsInProgress() ? 0 : (item->IsComplete() ? 1 : 2);
    std::string full_path = item->GetFullPath().ToString();
    g_download_progress_cb(id_, token, item->GetId(),
                           item->GetPercentComplete(),
                           item->GetReceivedBytes(),
                           item->GetTotalBytes(),
                           item->GetCurrentSpeed(),
                           state,
                           full_path.c_str());
    // Invalidate the token after the host's handler returns — the
    // CefDownloadItemCallback is per-invocation and shouldn't be retained.
    std::lock_guard<std::mutex> lock(g_download_progress_mu);
    g_download_progress_pending.erase(token);
}

void Exclr8CefOsrHandler::OnBeforeContextMenu(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        CefRefPtr<CefContextMenuParams> params,
        CefRefPtr<CefMenuModel> model) {
    if (!params || !model) return;

    auto localized = [](int string_id, const char* fallback) {
        CefRefPtr<CefResourceBundle> resources =
            CefResourceBundle::GetGlobal();
        if (resources) {
            CefString label = resources->GetLocalizedString(string_id);
            if (!label.empty()) return label;
        }
        return CefString(fallback);
    };
    auto ensure_separator_after = [&](size_t index) {
        if (index <= model->GetCount() &&
            (index == model->GetCount() ||
             model->GetTypeAt(index) != MENUITEMTYPE_SEPARATOR)) {
            model->InsertSeparatorAt(index);
        }
    };

    const CefString link_url = params->GetLinkUrl();
    if (!link_url.empty() &&
        model->GetIndexOf(kOpenLinkInNewTabCommandId) < 0) {
        model->InsertItemAt(
            0,
            kOpenLinkInNewTabCommandId,
            CefString("Open Link in New Tab"));
        ensure_separator_after(1);
    }

    if (params->IsEditable()) {
        const auto flags = params->GetEditStateFlags();
        struct EditCommand {
            int command_id;
            int label_id;
            const char* fallback;
            cef_context_menu_edit_state_flags_t enabled_flag;
        };
        const EditCommand commands[] = {
            {MENU_ID_UNDO, IDS_CONTENT_CONTEXT_UNDO, "Undo",
             CM_EDITFLAG_CAN_UNDO},
            {MENU_ID_REDO, IDS_CONTENT_CONTEXT_REDO, "Redo",
             CM_EDITFLAG_CAN_REDO},
            {MENU_ID_CUT, IDS_CONTENT_CONTEXT_CUT, "Cut",
             CM_EDITFLAG_CAN_CUT},
            {MENU_ID_COPY, IDS_CONTENT_CONTEXT_COPY, "Copy",
             CM_EDITFLAG_CAN_COPY},
            {MENU_ID_PASTE, IDS_CONTENT_CONTEXT_PASTE, "Paste",
             CM_EDITFLAG_CAN_PASTE},
            {MENU_ID_SELECT_ALL, IDS_CONTENT_CONTEXT_SELECTALL, "Select All",
             CM_EDITFLAG_CAN_SELECT_ALL},
        };

        size_t insertion_index = 0;
        for (const auto& command : commands) {
            if (model->GetIndexOf(command.command_id) >= 0) continue;
            model->InsertItemAt(
                insertion_index++,
                command.command_id,
                localized(command.label_id, command.fallback));
            model->SetEnabled(
                command.command_id,
                (flags & command.enabled_flag) != 0);
            if (command.command_id == MENU_ID_REDO ||
                command.command_id == MENU_ID_PASTE ||
                command.command_id == MENU_ID_SELECT_ALL) {
                ensure_separator_after(insertion_index++);
            }
        }
        return;
    }

    const auto page_edit_flags = params->GetEditStateFlags();
    const bool can_copy_selection =
        !params->GetSelectionText().empty() ||
        (page_edit_flags & CM_EDITFLAG_CAN_COPY) != 0;
    if (can_copy_selection &&
        model->GetIndexOf(MENU_ID_COPY) < 0) {
        model->InsertItemAt(
            0,
            MENU_ID_COPY,
            localized(IDS_CONTENT_CONTEXT_COPY, "Copy"));
        model->InsertSeparatorAt(1);
    }
}

bool Exclr8CefOsrHandler::RunContextMenu(CefRefPtr<CefBrowser> /*browser*/,
                                          CefRefPtr<CefFrame> /*frame*/,
                                          CefRefPtr<CefContextMenuParams> params,
                                          CefRefPtr<CefMenuModel> model,
                                          CefRefPtr<CefRunContextMenuCallback> callback) {
    if (!g_context_menu_cb) return false;  // fall back: suppress (no menu in OSR)
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_context_menu_mu);
        g_context_menu_pending[token] = PendingContextMenu{
            id_, callback, params->GetLinkUrl().ToString()};
    }

    // Serialize the complete menu hierarchy. Format:
    // "<id>\t<kind>\t<enabled>\t<checked>\t<depth>\t<label>" per line,
    // where kind maps command/check/radio/separator/submenu to 0/1/2/3/4.
    std::string items;
    std::function<void(CefRefPtr<CefMenuModel>, int)> append_model;
    append_model = [&](CefRefPtr<CefMenuModel> current, int depth) {
      const size_t count = current->GetCount();
      for (size_t i = 0; i < count; ++i) {
        const cef_menu_item_type_t t = current->GetTypeAt(i);
        const bool is_separator = t == MENUITEMTYPE_SEPARATOR;
        const bool is_submenu = t == MENUITEMTYPE_SUBMENU;
        const int id = is_separator ? 0 : current->GetCommandIdAt(i);
        const int kind = is_separator ? 3 :
                         is_submenu ? 4 :
                         t == MENUITEMTYPE_CHECK ? 1 :
                         t == MENUITEMTYPE_RADIO ? 2 : 0;
        const std::string label = is_separator
            ? std::string()
            : current->GetLabelAt(i).ToString();
        if (!items.empty()) items += '\n';
        items += std::to_string(id);
        items += '\t';
        items += std::to_string(kind);
        items += '\t';
        items += current->IsEnabledAt(i) ? '1' : '0';
        items += '\t';
        items += current->IsCheckedAt(i) ? '1' : '0';
        items += '\t';
        items += std::to_string(depth);
        items += '\t';
        items += label;

        if (is_submenu) {
          CefRefPtr<CefMenuModel> submenu = current->GetSubMenuAt(i);
          if (submenu) append_model(submenu, depth + 1);
        }
      }
    };
    append_model(model, 0);
    g_context_menu_cb(id_, token, params->GetXCoord(), params->GetYCoord(), items.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnFileDialog(CefRefPtr<CefBrowser> /*browser*/,
                                        FileDialogMode mode,
                                        const CefString& title,
                                        const CefString& default_file_path,
                                        const std::vector<CefString>& accept_filters,
                                        const std::vector<CefString>& /*accept_extensions*/,
                                        const std::vector<CefString>& /*accept_descriptions*/,
                                        CefRefPtr<CefFileDialogCallback> callback) {
    if (!g_file_dialog_cb) return false;  // fall back to CEF default
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_file_dialog_mu);
        g_file_dialog_pending[token] = PendingFileDialog{id_, callback};
    }
    // Join filters on '\n' for ABI simplicity — host splits on the other side.
    std::string filters;
    for (size_t i = 0; i < accept_filters.size(); ++i) {
        if (i) filters += '\n';
        filters += accept_filters[i].ToString();
    }
    std::string title_s = title.ToString();
    std::string default_s = default_file_path.ToString();
    g_file_dialog_cb(id_, token, static_cast<int>(mode),
                     title_s.c_str(), default_s.c_str(),
                     filters.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnCursorChange(CefRefPtr<CefBrowser> /*browser*/,
                                         CefCursorHandle /*cursor*/,
                                         cef_cursor_type_t type,
                                         const CefCursorInfo& /*custom_cursor_info*/) {
    if (g_cursor_change_cb) {
        g_cursor_change_cb(id_, static_cast<int>(type));
    }
    // Return true: host (Avalonia) is responsible for setting the platform cursor.
    return true;
}

bool Exclr8CefOsrHandler::OnConsoleMessage(CefRefPtr<CefBrowser> /*browser*/,
                                            cef_log_severity_t level,
                                            const CefString& message,
                                            const CefString& source,
                                            int line) {
    if (g_console_message_cb) {
        std::string msg = message.ToString();
        std::string src = source.ToString();
        g_console_message_cb(id_, static_cast<int>(level),
                             msg.c_str(), src.c_str(), line);
    }
    // Return false to let Chromium also emit its default console output.
    return false;
}

void Exclr8CefOsrHandler::OnAfterCreated(CefRefPtr<CefBrowser> browser) {
    {
        std::lock_guard<std::mutex> lock(browser_mu_);
        browser_ = browser;
    }
    browser->GetHost()->WasResized();
    if (g_browser_initialized_cb) {
        g_browser_initialized_cb(id_);
    }
}

bool Exclr8CefOsrHandler::OnBeforePopup(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        int /*popup_id*/,
        const CefString& target_url,
        const CefString& target_frame_name,
        cef_window_open_disposition_t target_disposition,
        bool user_gesture,
        const CefPopupFeatures& /*popupFeatures*/,
        CefWindowInfo& /*windowInfo*/,
        CefRefPtr<CefClient>& /*client*/,
        CefBrowserSettings& /*settings*/,
        CefRefPtr<CefDictionaryValue>& extra_info,
        bool* /*no_javascript_access*/) {
    extra_info = CreateBrowserExtraInfo();
    return ForwardNewTabRequest(
        target_url,
        target_frame_name,
        target_disposition,
        user_gesture);
}

bool Exclr8CefOsrHandler::OnOpenURLFromTab(
        CefRefPtr<CefBrowser> /*browser*/,
        CefRefPtr<CefFrame> /*frame*/,
        const CefString& target_url,
        WindowOpenDisposition target_disposition,
        bool user_gesture) {
    switch (target_disposition) {
        case CEF_WOD_NEW_FOREGROUND_TAB:
        case CEF_WOD_NEW_BACKGROUND_TAB:
        case CEF_WOD_NEW_POPUP:
        case CEF_WOD_NEW_WINDOW:
            return ForwardNewTabRequest(
                target_url,
                CefString(),
                target_disposition,
                user_gesture);
        default:
            return false;
    }
}

bool Exclr8CefOsrHandler::ForwardNewTabRequest(
        const CefString& target_url,
        const CefString& target_frame_name,
        cef_window_open_disposition_t target_disposition,
        bool user_gesture) {
    if (!g_before_popup_cb || target_url.empty()) {
        return false;
    }

    std::string url = target_url.ToString();
    std::string frame_name = target_frame_name.ToString();
    g_before_popup_cb(
        id_,
        url.c_str(),
        frame_name.c_str(),
        static_cast<int>(target_disposition),
        user_gesture ? 1 : 0);
    return true;
}

void Exclr8CefOsrHandler::OnBeforeClose(CefRefPtr<CefBrowser> /*browser*/) {
    int closed_id = id_;

    // Cancel any pending deferred-response callbacks owned by this browser.
    // Without this, the CEF callback objects stay refcounted in our registry
    // until process exit; worse, if the host eventually resolves the token,
    // we'd try to invoke a callback on a dead browser.
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        for (auto it = g_js_dialog_pending.begin(); it != g_js_dialog_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Continue(false, CefString());
                it = g_js_dialog_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_file_dialog_mu);
        for (auto it = g_file_dialog_pending.begin(); it != g_file_dialog_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_file_dialog_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_context_menu_mu);
        for (auto it = g_context_menu_pending.begin(); it != g_context_menu_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_context_menu_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_download_starting_mu);
        for (auto it = g_download_starting_pending.begin(); it != g_download_starting_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                // Do NOT Continue() with an empty path — per CEF that means
                // "download to the suggested name in the default temp dir",
                // not cancel. Dropping the callback unexecuted (plus the
                // browser itself closing) abandons the download.
                it = g_download_starting_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_download_progress_mu);
        for (auto it = g_download_progress_pending.begin(); it != g_download_progress_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                it = g_download_progress_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_cancelled_downloads_mu);
        for (auto it = g_cancelled_downloads.begin(); it != g_cancelled_downloads.end(); ) {
            if (it->first == closed_id) it = g_cancelled_downloads.erase(it);
            else ++it;
        }
    }
    {
        // Drop the browser's stashed LoadString document (if any).
        std::lock_guard<std::mutex> lock(g_loadstring_mu);
        g_loadstring_docs.erase(closed_id);
    }
    {
        std::lock_guard<std::mutex> lock(g_auth_mu);
        for (auto it = g_auth_pending.begin(); it != g_auth_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_auth_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        // Resolve any pending scheme requests for this browser with a 410
        // so the CefCallback gets unblocked rather than leaking.
        std::lock_guard<std::mutex> lock(g_scheme_mu);
        for (auto it = g_scheme_pending.begin(); it != g_scheme_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.handler)
                    it->second.handler->Resolve(410, "Gone", "text/plain", {});
                it = g_scheme_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        // Same sweep for the streaming URL-handler registry — without it,
        // pending entries (and their CefCallbacks) leak on browser close.
        std::lock_guard<std::mutex> lock(g_url_handler_mu);
        for (auto it = g_url_handler_pending.begin(); it != g_url_handler_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.handler)
                    it->second.handler->Resolve(410, "Gone", "text/plain", {}, {});
                it = g_url_handler_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_resource_request_mu);
        for (auto it = g_resource_request_pending.begin(); it != g_resource_request_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_resource_request_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_permission_prompt_mu);
        for (auto it = g_permission_prompt_pending.begin(); it != g_permission_prompt_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback)
                    it->second.callback->Continue(CEF_PERMISSION_RESULT_DISMISS);
                it = g_permission_prompt_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_media_access_mu);
        for (auto it = g_media_access_pending.begin(); it != g_media_access_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_media_access_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_cert_error_mu);
        for (auto it = g_cert_error_pending.begin(); it != g_cert_error_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_cert_error_pending.erase(it);
            } else {
                ++it;
            }
        }
    }

    // If a drag is in flight when the browser closes, drop the drag state
    // (releases the CefDragData ref) and tell the host to clear any drag
    // overlay it was rendering.
    if (in_drag_) {
        clear_drag();
        if (g_drag_image_cb) g_drag_image_cb(closed_id, nullptr, 0, 0, 0, 0);
    }
    // Drop the DevTools observer registration for this browser (if any).
    {
        std::lock_guard<std::mutex> lock(g_devtools_observers_mu);
        g_devtools_observers.erase(closed_id);
    }
    // Drop any pending JS-invoke entries for this browser. The renderer
    // Promise stays pending in the dead context — no leak in the browser
    // process.
    {
        std::lock_guard<std::mutex> lock(g_js_invoke_mu);
        for (auto it = g_js_invoke_pending.begin(); it != g_js_invoke_pending.end(); ) {
            if (it->second.browser_id == closed_id) it = g_js_invoke_pending.erase(it);
            else ++it;
        }
    }

    {
        std::lock_guard<std::mutex> lock(browser_mu_);
        browser_ = nullptr;
    }
    {
        std::lock_guard<std::mutex> lock(g_osr_browsers_mu);
        g_osr_browsers.erase(id_);
    }
    if (g_browser_closed_cb) {
        g_browser_closed_cb(closed_id);
    }
}

void Exclr8CefOsrHandler::OnAddressChange(CefRefPtr<CefBrowser> /*browser*/,
                                           CefRefPtr<CefFrame> frame,
                                           const CefString& url) {
    if (!frame->IsMain()) return;
    if (g_address_cb) {
        std::string s = url.ToString();
        g_address_cb(id_, s.c_str());
    }
}

void Exclr8CefOsrHandler::OnTitleChange(CefRefPtr<CefBrowser> /*browser*/,
                                         const CefString& title) {
    if (g_title_cb) {
        std::string s = title.ToString();
        g_title_cb(id_, s.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadingStateChange(CefRefPtr<CefBrowser> /*browser*/,
                                                bool isLoading,
                                                bool canGoBack,
                                                bool canGoForward) {
    if (g_loading_state_cb) {
        g_loading_state_cb(id_,
                           isLoading ? 1 : 0,
                           canGoBack ? 1 : 0,
                           canGoForward ? 1 : 0);
    }
}

void Exclr8CefOsrHandler::OnLoadStart(CefRefPtr<CefBrowser> /*browser*/,
                                       CefRefPtr<CefFrame> frame,
                                       TransitionType /*transition_type*/) {
    if (g_load_start_cb) {
        std::string url = frame->GetURL().ToString();
        g_load_start_cb(id_, frame->IsMain() ? 1 : 0, url.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadEnd(CefRefPtr<CefBrowser> /*browser*/,
                                     CefRefPtr<CefFrame> frame,
                                     int httpStatusCode) {
    if (g_load_end_cb) {
        std::string url = frame->GetURL().ToString();
        g_load_end_cb(id_, frame->IsMain() ? 1 : 0, url.c_str(), httpStatusCode);
    }
}

void Exclr8CefOsrHandler::OnLoadError(CefRefPtr<CefBrowser> /*browser*/,
                                       CefRefPtr<CefFrame> frame,
                                       ErrorCode errorCode,
                                       const CefString& errorText,
                                       const CefString& failedUrl) {
    if (g_load_error_cb) {
        std::string text = errorText.ToString();
        std::string failed = failedUrl.ToString();
        g_load_error_cb(id_, frame->IsMain() ? 1 : 0,
                        static_cast<int>(errorCode),
                        text.c_str(), failed.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadingProgressChange(CefRefPtr<CefBrowser> /*browser*/,
                                                   double progress) {
    if (g_loading_progress_cb) {
        g_loading_progress_cb(id_, progress);
    }
}

void Exclr8CefOsrHandler::OnStatusMessage(CefRefPtr<CefBrowser> /*browser*/,
                                           const CefString& value) {
    if (g_status_message_cb) {
        std::string s = value.ToString();
        g_status_message_cb(id_, s.c_str());
    }
}

bool Exclr8CefOsrHandler::OnTooltip(CefRefPtr<CefBrowser> /*browser*/,
                                     CefString& text) {
    if (g_tooltip_cb) {
        std::string s = text.ToString();
        g_tooltip_cb(id_, s.c_str());
    }
    // Return false so Chromium can also render its default tooltip; hosts
    // that want to fully take over should suppress at the C# layer.
    return false;
}

void Exclr8CefOsrHandler::OnFaviconURLChange(CefRefPtr<CefBrowser> /*browser*/,
                                              const std::vector<CefString>& icon_urls) {
    if (g_favicon_cb) {
        // We pass only the first URL; CEF orders them by browser-preference
        // (highest-resolution PNG ahead of .ico), so the first is the most
        // useful for typical tab-strip / window-icon use.
        std::string first = icon_urls.empty() ? std::string() : icon_urls.front().ToString();
        g_favicon_cb(id_, first.c_str());
    }
}

void Exclr8CefOsrHandler::OnFullscreenModeChange(CefRefPtr<CefBrowser> /*browser*/,
                                                  bool fullscreen) {
    if (g_fullscreen_cb) {
        g_fullscreen_cb(id_, fullscreen ? 1 : 0);
    }
}

namespace {
class OsrResizeTask final : public CefTask {
public:
    explicit OsrResizeTask(CefRefPtr<Exclr8CefOsrHandler> handler)
        : handler_(std::move(handler)) {}

    void Execute() override {
        if (handler_) handler_->ApplyPendingSizeOnUi();
    }

private:
    CefRefPtr<Exclr8CefOsrHandler> handler_;
    IMPLEMENT_REFCOUNTING(OsrResizeTask);
};

class SettledOsrResizeTask final : public CefTask {
public:
    SettledOsrResizeTask(
        CefRefPtr<Exclr8CefOsrHandler> handler,
        uint64_t generation)
        : handler_(std::move(handler)), generation_(generation) {}

    void Execute() override {
        if (handler_) handler_->ApplySettledSizeOnUi(generation_);
    }

private:
    CefRefPtr<Exclr8CefOsrHandler> handler_;
    uint64_t generation_;
    IMPLEMENT_REFCOUNTING(SettledOsrResizeTask);
};
}  // namespace

void Exclr8CefOsrHandler::SetSize(int width, int height) {
    {
        std::lock_guard<std::mutex> lock(size_mu_);
        width_ = width;
        height_ = height;
    }
    resize_generation_.fetch_add(1, std::memory_order_acq_rel);
    QueuePendingSizeOnUi();
    QueueSettledSizeOnUi();
}

void Exclr8CefOsrHandler::QueuePendingSizeOnUi() {
    bool expected = false;
    if (!resize_task_pending_.compare_exchange_strong(
            expected,
            true,
            std::memory_order_acq_rel)) {
        return;
    }

    if (CefCurrentlyOn(TID_UI)) {
        ApplyPendingSizeOnUi();
        return;
    }

    auto task = CefRefPtr<CefTask>(new OsrResizeTask(this));
    if (!CefPostTask(TID_UI, task)) {
        resize_task_pending_.store(false, std::memory_order_release);
    }
}

void Exclr8CefOsrHandler::ApplyPendingSizeOnUi() {
    int applied_width;
    int applied_height;
    {
        std::lock_guard<std::mutex> lock(size_mu_);
        applied_width = width_;
        applied_height = height_;
    }

    // CefBrowserHost::WasResized is a CEF-UI-thread operation. Keeping one
    // task outstanding collapses a fast splitter drag to the latest size
    // instead of leaving Chromium to drain a long queue after the drag.
    auto b = browser();
    if (b) b->GetHost()->WasResized();

    resize_task_pending_.store(false, std::memory_order_release);
    bool size_changed;
    {
        std::lock_guard<std::mutex> lock(size_mu_);
        size_changed = width_ != applied_width || height_ != applied_height;
    }
    if (size_changed) {
        QueuePendingSizeOnUi();
    }
}

void Exclr8CefOsrHandler::QueueSettledSizeOnUi() {
    bool expected = false;
    if (!settled_resize_task_pending_.compare_exchange_strong(
            expected,
            true,
            std::memory_order_acq_rel)) {
        return;
    }

    const uint64_t generation =
        resize_generation_.load(std::memory_order_acquire);
    if (!PostSettledSizeTask(generation)) {
        settled_resize_task_pending_.store(
            false,
            std::memory_order_release);
    }
}

bool Exclr8CefOsrHandler::PostSettledSizeTask(uint64_t generation) {
    constexpr int64_t kSettledResizeDelayMilliseconds = 75;
    auto task = CefRefPtr<CefTask>(
        new SettledOsrResizeTask(this, generation));
    return CefPostDelayedTask(
        TID_UI,
        task,
        kSettledResizeDelayMilliseconds);
}

void Exclr8CefOsrHandler::ApplySettledSizeOnUi(
    uint64_t scheduled_generation) {
    const uint64_t current_generation =
        resize_generation_.load(std::memory_order_acquire);
    if (current_generation != scheduled_generation) {
        // The splitter is still moving. Keep a single trailing task and
        // restart its quiet period from the newest observed size.
        if (!PostSettledSizeTask(current_generation)) {
            settled_resize_task_pending_.store(
                false,
                std::memory_order_release);
        }
        return;
    }

    auto b = browser();
    if (b) {
        // Reaffirm the final size after Chromium's resize pipeline has gone
        // quiet, then force a full frame. Old-size accelerated frames can
        // otherwise arrive last and leave a static page stuck at an
        // intermediate splitter position indefinitely.
        b->GetHost()->WasResized();
        b->GetHost()->Invalidate(PET_VIEW);
    }

    settled_resize_task_pending_.store(false, std::memory_order_release);
    if (resize_generation_.load(std::memory_order_acquire)
        != current_generation) {
        QueueSettledSizeOnUi();
    }
}

void Exclr8CefOsrHandler::SetDeviceScaleFactor(float scale) {
    if (scale <= 0.0f) return;
    {
        std::lock_guard<std::mutex> lock(size_mu_);
        if (scale == device_scale_factor_) return;
        device_scale_factor_ = scale;
    }
    auto b = browser();
    if (b) b->GetHost()->NotifyScreenInfoChanged();
}

bool Exclr8CefOsrHandler::StartDragging(CefRefPtr<CefBrowser> browser,
                                         CefRefPtr<CefDragData> drag_data,
                                         DragOperationsMask allowed_ops,
                                         int x, int y) {
    drag_data_ = drag_data;
    drag_allowed_ops_ = allowed_ops;
    drag_current_op_ = DRAG_OPERATION_NONE;
    in_drag_ = true;

    // Ship the drag preview bitmap to the host so it can overlay it while
    // the drag is in flight. CEF in OSR mode never paints this itself.
    if (g_drag_image_cb) {
        CefRefPtr<CefImage> img = drag_data->HasImage() ? drag_data->GetImage() : nullptr;
        if (img && !img->IsEmpty()) {
            int pw = 0, ph = 0;
            CefRefPtr<CefBinaryValue> bin = img->GetAsBitmap(
                device_scale_factor_, CEF_COLOR_TYPE_BGRA_8888,
                CEF_ALPHA_TYPE_PREMULTIPLIED, pw, ph);
            if (bin && pw > 0 && ph > 0) {
                std::vector<unsigned char> buf(bin->GetSize());
                bin->GetData(buf.data(), buf.size(), 0);
                CefPoint hs = drag_data->GetImageHotspot();
                g_drag_image_cb(id_, buf.data(), pw, ph, hs.x, hs.y);
            } else {
                g_drag_image_cb(id_, nullptr, 0, 0, 0, 0);
            }
        } else {
            g_drag_image_cb(id_, nullptr, 0, 0, 0, 0);
        }
    }

    // Give the host a chance to drive an OS-level drag. Decompose the drag
    // data into individual fields — CefDragData doesn't cross the C ABI.
    if (g_start_drag_cb) {
        std::string text = drag_data->GetFragmentText().ToString();
        std::string html = drag_data->GetFragmentHtml().ToString();
        std::string link_url = drag_data->GetLinkURL().ToString();
        std::string link_title = drag_data->GetLinkTitle().ToString();
        std::vector<CefString> files;
        drag_data->GetFileNames(files);
        std::vector<std::string> file_storage;
        file_storage.reserve(files.size());
        std::vector<const char*> file_ptrs;
        file_ptrs.reserve(files.size());
        for (const auto& f : files) {
            file_storage.push_back(f.ToString());
            file_ptrs.push_back(file_storage.back().c_str());
        }

        int handled = g_start_drag_cb(
            id_, static_cast<int>(allowed_ops), x, y,
            text.c_str(), html.c_str(),
            link_url.c_str(), link_title.c_str(),
            file_ptrs.empty() ? nullptr : file_ptrs.data(),
            static_cast<int>(file_ptrs.size()));
        if (handled) {
            // Host owns the drag. It MUST eventually call
            // excef_drag_source_ended_at + excef_drag_source_system_drag_ended.
            return true;
        }
    }

    // Fallback: self-target. For internal-only DnD this is the same browser;
    // full OS-level DnD would route through the host's window system.
    CefMouseEvent ev;
    ev.x = x;
    ev.y = y;
    browser->GetHost()->DragTargetDragEnter(drag_data, ev, allowed_ops);
    return true;
}

void Exclr8CefOsrHandler::UpdateDragCursor(CefRefPtr<CefBrowser> /*browser*/,
                                            DragOperation operation) {
    drag_current_op_ = operation;
}

CefRefPtr<Exclr8CefOsrHandler> LookupOsrHandler(int browser_id) {
    std::lock_guard<std::mutex> lock(g_osr_browsers_mu);
    auto it = g_osr_browsers.find(browser_id);
    return it == g_osr_browsers.end() ? nullptr : it->second;
}

CefRefPtr<CefBrowser> GetOsrBrowser(int browser_id) {
    auto handler = LookupOsrHandler(browser_id);
    return handler ? handler->browser() : nullptr;
}

int AllocateBrowserId() { return g_next_id++; }
void RegisterOsrHandler(int browser_id, CefRefPtr<Exclr8CefOsrHandler> handler) {
    std::lock_guard<std::mutex> lock(g_osr_browsers_mu);
    g_osr_browsers[browser_id] = std::move(handler);
}
void UnregisterOsrHandler(int browser_id) {
    excef_stop_external_begin_frame_clock(browser_id);
    std::lock_guard<std::mutex> lock(g_osr_browsers_mu);
    g_osr_browsers.erase(browser_id);
}

}  // namespace exclr8cef

// ---- C ABI implementations ------------------------------------------------

namespace {
// Shared browser-create core for all OSR-create variants. `flags` is a
// bitmask: bit 0 = external_begin_frame_enabled, bit 1 = shared_texture_enabled
// (accelerated paint).
int CreateOffscreenBrowserImpl(int width, int height,
                                float device_scale_factor,
                                const char* url,
                                excef_paint_callback_t paint,
                                CefRefPtr<CefRequestContext> request_context,
                                int flags) {
    if (!url || width <= 0 || height <= 0) return 0;

    int id = exclr8cef::AllocateBrowserId();
    auto handler = CefRefPtr<exclr8cef::Exclr8CefOsrHandler>(
        new exclr8cef::Exclr8CefOsrHandler(id, width, height,
                                            device_scale_factor, paint));
    exclr8cef::RegisterOsrHandler(id, handler);

    CefWindowInfo window_info;
    window_info.SetAsWindowless((CefWindowHandle)0);
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    if (flags & 0x1) window_info.external_begin_frame_enabled = true;
    if (flags & 0x2) window_info.shared_texture_enabled = true;

    CefBrowserSettings browser_settings;
    browser_settings.windowless_frame_rate = 30;

    if (!CefBrowserHost::CreateBrowser(window_info, handler.get(), url,
                                       browser_settings, exclr8cef::CreateBrowserExtraInfo(),
                                       request_context)) {
        exclr8cef::UnregisterOsrHandler(id);
        return 0;
    }
    return id;
}
}  // namespace

extern "C" int excef_create_offscreen_browser(int width, int height,
                                              float device_scale_factor,
                                              const char* url,
                                              excef_paint_callback_t paint) {
    return CreateOffscreenBrowserImpl(width, height, device_scale_factor,
                                       url, paint, /*request_context=*/nullptr, /*flags=*/0);
}

extern "C" int excef_create_offscreen_browser_in_context(
        int width, int height, float device_scale_factor,
        const char* url, excef_paint_callback_t paint,
        int context_handle) {
    CefRefPtr<CefRequestContext> ctx;
    if (context_handle != 0) {
        std::lock_guard<std::mutex> lock(exclr8cef::g_request_contexts_mu);
        auto it = exclr8cef::g_request_contexts.find(context_handle);
        if (it == exclr8cef::g_request_contexts.end()) return 0;  // unknown handle
        ctx = it->second;
    }
    return CreateOffscreenBrowserImpl(width, height, device_scale_factor,
                                       url, paint, ctx, /*flags=*/0);
}

extern "C" int excef_create_offscreen_browser_ex(
        int width, int height, float device_scale_factor,
        const char* url, excef_paint_callback_t paint,
        int context_handle, int flags) {
    CefRefPtr<CefRequestContext> ctx;
    if (context_handle != 0) {
        std::lock_guard<std::mutex> lock(exclr8cef::g_request_contexts_mu);
        auto it = exclr8cef::g_request_contexts.find(context_handle);
        if (it == exclr8cef::g_request_contexts.end()) return 0;
        ctx = it->second;
    }
    return CreateOffscreenBrowserImpl(width, height, device_scale_factor,
                                       url, paint, ctx, flags);
}

extern "C" int excef_create_request_context(const char* cache_path) {
    CefRequestContextSettings settings;
    if (cache_path && *cache_path) {
        // CEF canonicalizes its root, but compares request-context paths as
        // supplied. macOS /var and /private/var must identify the same root.
        std::error_code error;
        const auto canonical_path = std::filesystem::weakly_canonical(cache_path, error);
        if (error || !canonical_path.is_absolute()) return 0;
        CefString(&settings.cache_path).FromString(canonical_path.string());
        // GhostSHELL supplies disk paths only for retained profiles. The global
        // context is in-memory, so its cookie setting does not retain sessions
        // in these independent request contexts. Private contexts stay transient.
        settings.persist_session_cookies = true;
    }
    CefRefPtr<CefRequestContext> ctx = CefRequestContext::CreateContext(
        settings, /*handler=*/nullptr);
    if (!ctx) return 0;

    int handle = exclr8cef::g_next_context_handle.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_request_contexts_mu);
        exclr8cef::g_request_contexts[handle] = ctx;
    }
    return handle;
}

namespace exclr8cef {
CefRefPtr<CefRequestContext> ResolveContext(int handle) {
    if (handle == 0) return CefRequestContext::GetGlobalContext();
    std::lock_guard<std::mutex> lock(g_request_contexts_mu);
    auto it = g_request_contexts.find(handle);
    if (it == g_request_contexts.end()) return nullptr;
    return it->second;
}
}  // namespace exclr8cef

namespace {
// Shadow alias so the dozen existing callers below (in the anon namespace)
// keep working with the unqualified name.
inline CefRefPtr<CefRequestContext> ResolveContext(int handle) {
    return exclr8cef::ResolveContext(handle);
}
}  // namespace

extern "C" int excef_set_preference(int context_handle,
                                      const char* name,
                                      const char* value_json) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx || !name) return 0;
    CefRefPtr<CefValue> value;
    if (value_json && *value_json) {
        value = CefParseJSON(value_json, JSON_PARSER_RFC);
    } else {
        value = CefValue::Create();
        value->SetNull();
    }
    if (!value) return 0;
    CefString err;
    return ctx->SetPreference(name, value, err) ? 1 : 0;
}

extern "C" const char* excef_get_preference(int context_handle, const char* name) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx || !name) return nullptr;
    if (!ctx->HasPreference(name)) return nullptr;
    CefRefPtr<CefValue> value = ctx->GetPreference(name);
    if (!value) return nullptr;
    CefString json = CefWriteJSON(value, JSON_WRITER_DEFAULT);
    std::string s = json.ToString();
    char* out = static_cast<char*>(std::malloc(s.size() + 1));
    if (!out) return nullptr;
    std::memcpy(out, s.data(), s.size());
    out[s.size()] = 0;
    return out;
}

extern "C" void excef_free_string(const char* s) {
    if (s) std::free(const_cast<char*>(s));
}

extern "C" int excef_clear_http_auth_credentials(int context_handle) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx) return 0;
    ctx->ClearHttpAuthCredentials(nullptr);
    return 1;
}

namespace {

class RequestContextCompletionRelay : public CefCompletionCallback {
public:
    explicit RequestContextCompletionRelay(int request_id)
        : request_id_(request_id) {}

    void OnComplete() override {
        if (exclr8cef::g_request_context_completion_cb)
            exclr8cef::g_request_context_completion_cb(request_id_, 1);
    }

private:
    int request_id_;
    IMPLEMENT_REFCOUNTING(RequestContextCompletionRelay);
};

class CookieDeletionRelay : public CefDeleteCookiesCallback {
public:
    explicit CookieDeletionRelay(int request_id)
        : request_id_(request_id) {}

    void OnComplete(int num_deleted) override {
        if (exclr8cef::g_request_context_completion_cb)
            exclr8cef::g_request_context_completion_cb(request_id_, num_deleted);
    }

private:
    int request_id_;
    IMPLEMENT_REFCOUNTING(CookieDeletionRelay);
};

}  // namespace

extern "C" void excef_set_request_context_completion_callback(
    excef_request_context_completion_cb_t cb) {
    exclr8cef::g_request_context_completion_cb = cb;
}

extern "C" int excef_clear_http_auth_credentials_async(
    int context_handle, int request_id) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx) return 0;
    ctx->ClearHttpAuthCredentials(new RequestContextCompletionRelay(request_id));
    return 1;
}

extern "C" int excef_close_all_connections_async(
    int context_handle, int request_id) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx) return 0;
    ctx->CloseAllConnections(new RequestContextCompletionRelay(request_id));
    return 1;
}

extern "C" int excef_close_all_connections(int context_handle) {
    auto ctx = ResolveContext(context_handle);
    if (!ctx) return 0;
    ctx->CloseAllConnections(nullptr);
    return 1;
}

extern "C" void excef_release_request_context(int handle) {
    if (handle == 0) return;
    std::lock_guard<std::mutex> lock(exclr8cef::g_request_contexts_mu);
    exclr8cef::g_request_contexts.erase(handle);
    // CEF keeps the context alive via the browsers using it; once they
    // close, it gets torn down.
}

extern "C" void excef_resize_offscreen_browser(int browser_id,
                                               int width, int height) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    h->SetSize(width, height);
}

extern "C" void excef_set_device_scale_factor(int browser_id, float scale) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    h->SetDeviceScaleFactor(scale);
}

extern "C" void excef_set_zoom_level(int browser_id, double level) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto browser = h->browser();
    if (browser) browser->GetHost()->SetZoomLevel(level);
}

extern "C" double excef_get_zoom_level(int browser_id) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return 0.0;
    auto browser = h->browser();
    return browser ? browser->GetHost()->GetZoomLevel() : 0.0;
}

extern "C" void excef_notify_move_or_resize_started(int browser_id) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto browser = h->browser();
    if (browser) browser->GetHost()->NotifyMoveOrResizeStarted();
}

extern "C" void excef_notify_screen_info_changed(int browser_id) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto browser = h->browser();
    if (browser) browser->GetHost()->NotifyScreenInfoChanged();
}

extern "C" void excef_replace_misspelling(int browser_id, const char* word) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto browser = h->browser();
    if (browser) browser->GetHost()->ReplaceMisspelling(CefString(word ? word : ""));
}

extern "C" void excef_add_word_to_dictionary(int browser_id, const char* word) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto browser = h->browser();
    if (browser) browser->GetHost()->AddWordToDictionary(CefString(word ? word : ""));
}

extern "C" void excef_set_frame_lifecycle_callback(excef_frame_lifecycle_cb_t cb) {
    exclr8cef::g_frame_lifecycle_cb = cb;
}

extern "C" void excef_set_main_frame_changed_callback(excef_main_frame_changed_cb_t cb) {
    exclr8cef::g_main_frame_changed_cb = cb;
}

extern "C" void excef_enable_audio_capture(int browser_id, int enable) {
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_audio_enabled_mu);
        exclr8cef::g_audio_enabled[browser_id] = enable != 0;
    }
    // Force CEF to re-query handlers so the toggle takes effect on the
    // next stream-start. There's no direct "rebind" call; closing the
    // browser or starting/stopping media triggers re-query. The host can
    // call WasResized to nudge a layout/handler refresh.
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (h) {
        auto b = h->browser();
        if (b) b->GetHost()->WasResized();
    }
}

extern "C" void excef_set_audio_stream_started_callback(excef_audio_stream_started_cb_t cb) {
    exclr8cef::g_audio_started_cb = cb;
}

extern "C" void excef_set_audio_stream_packet_callback(excef_audio_stream_packet_cb_t cb) {
    exclr8cef::g_audio_packet_cb = cb;
}

extern "C" void excef_set_audio_stream_stopped_callback(excef_audio_stream_stopped_cb_t cb) {
    exclr8cef::g_audio_stopped_cb = cb;
}

extern "C" void excef_set_audio_stream_error_callback(excef_audio_stream_error_cb_t cb) {
    exclr8cef::g_audio_error_cb = cb;
}

extern "C" void excef_set_audio_muted(int browser_id, int muted) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return;
    auto b = h->browser();
    if (b) b->GetHost()->SetAudioMuted(muted != 0);
}

extern "C" int excef_is_audio_muted(int browser_id) {
    auto h = exclr8cef::LookupOsrHandler(browser_id);
    if (!h) return 0;
    auto b = h->browser();
    return (b && b->GetHost()->IsAudioMuted()) ? 1 : 0;
}

extern "C" void excef_set_should_filter_response_callback(excef_should_filter_response_cb_t cb) {
    exclr8cef::g_should_filter_cb = cb;
}
extern "C" void excef_set_response_filter_callback(excef_response_filter_cb_t cb) {
    exclr8cef::g_response_filter_cb = cb;
}
extern "C" void excef_set_response_filter_finalize_callback(excef_response_filter_finalize_cb_t cb) {
    exclr8cef::g_response_filter_finalize_cb = cb;
}
extern "C" void excef_set_chrome_command_callback(excef_chrome_command_cb_t cb) {
    exclr8cef::g_chrome_command_cb = cb;
}
extern "C" void excef_set_app_menu_visible_callback(excef_app_menu_visibility_cb_t cb) {
    exclr8cef::g_app_menu_visible_cb = cb;
}
extern "C" void excef_set_app_menu_enabled_callback(excef_app_menu_visibility_cb_t cb) {
    exclr8cef::g_app_menu_enabled_cb = cb;
}
extern "C" void excef_set_page_action_visible_callback(excef_page_action_visibility_cb_t cb) {
    exclr8cef::g_page_action_visible_cb = cb;
}
extern "C" void excef_set_toolbar_button_visible_callback(excef_toolbar_button_visibility_cb_t cb) {
    exclr8cef::g_toolbar_button_visible_cb = cb;
}
extern "C" void excef_set_should_handle_resource_callback(excef_should_handle_resource_cb_t cb) {
    exclr8cef::g_should_handle_resource_cb = cb;
}
extern "C" void excef_set_touch_handle_size_callback(excef_touch_handle_size_cb_t cb) {
    exclr8cef::g_touch_handle_size_cb = cb;
}
extern "C" void excef_set_touch_handle_state_callback(excef_touch_handle_state_cb_t cb) {
    exclr8cef::g_touch_handle_state_cb = cb;
}
extern "C" void excef_set_ime_composition_range_callback(excef_ime_composition_range_cb_t cb) {
    exclr8cef::g_ime_composition_range_cb = cb;
}
extern "C" void excef_set_virtual_keyboard_callback(excef_virtual_keyboard_cb_t cb) {
    exclr8cef::g_virtual_keyboard_cb = cb;
}
extern "C" void excef_set_accelerated_paint_callback(excef_accelerated_paint_cb_t cb) {
    exclr8cef::g_accelerated_paint_cb = cb;
}

extern "C" void excef_resolve_resource_handler_request(
        uint64_t token,
        int status_code,
        const char* status_text,
        const char* mime_type,
        const char* headers,
        const unsigned char* body,
        int body_len) {
    // Copy body + parse headers up front (so we can move them into either
    // the pending placeholder or the handler instance without holding the
    // lock while doing string work).
    std::vector<uint8_t> body_copy;
    if (body && body_len > 0) body_copy.assign(body, body + body_len);
    std::vector<std::pair<std::string, std::string>> hdrs;
    if (headers && *headers) {
        std::string s(headers);
        size_t start = 0;
        for (size_t i = 0; i <= s.size(); ++i) {
            if (i == s.size() || s[i] == '\n') {
                if (i > start) {
                    auto line = s.substr(start, i - start);
                    auto colon = line.find(':');
                    if (colon != std::string::npos) {
                        std::string k = line.substr(0, colon);
                        std::string v = line.substr(colon + 1);
                        if (!v.empty() && v.front() == ' ') v.erase(0, 1);
                        hdrs.emplace_back(std::move(k), std::move(v));
                    }
                }
                start = i + 1;
            }
        }
    }
    std::string st = status_text ? status_text : "";
    std::string mt = mime_type ? mime_type : "text/plain";

    CefRefPtr<exclr8cef::UrlResourceHandler> handler;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_url_handler_mu);
        auto it = exclr8cef::g_url_handler_pending.find(token);
        if (it == exclr8cef::g_url_handler_pending.end()) return;
        if (it->second.handler) {
            // Async path — Open already ran, the handler is alive and
            // waiting on its CefCallback. Hand off and let Resolve fire.
            handler = it->second.handler;
            exclr8cef::g_url_handler_pending.erase(it);
        } else {
            // Sync path — Open hasn't been called yet. Stash the response
            // in the placeholder; Open will consume it.
            it->second.resolved = true;
            it->second.status_code = status_code;
            it->second.status_text = std::move(st);
            it->second.mime_type = std::move(mt);
            it->second.body = std::move(body_copy);
            it->second.extra_headers = std::move(hdrs);
            return;
        }
    }
    handler->Resolve(status_code, std::move(st), std::move(mt),
                      std::move(body_copy), std::move(hdrs));
}

namespace {

CefRefPtr<CefFrame> get_focused_frame(int browser_id) {
    auto browser = exclr8cef::GetOsrBrowser(browser_id);
    if (!browser) return nullptr;
    auto frame = browser->GetFocusedFrame();
    return frame ? frame : browser->GetMainFrame();
}

}  // namespace

extern "C" void excef_copy(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Copy();
}

extern "C" void excef_paste(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Paste();
}

extern "C" void excef_cut(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Cut();
}

extern "C" void excef_select_all(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->SelectAll();
}

extern "C" void excef_undo(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Undo();
}

extern "C" void excef_redo(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Redo();
}

namespace {

CefRefPtr<CefBrowser> get_browser(int browser_id) {
    return exclr8cef::GetOsrBrowser(browser_id);
}

}  // namespace

// ---- Input ----------------------------------------------------------------

extern "C" void excef_send_mouse_move(int browser_id, int x, int y,
                                      int modifiers, int mouse_leave) {
    auto handler = exclr8cef::LookupOsrHandler(browser_id);
    if (!handler) return;
    auto b = handler->browser();
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    if (handler->is_in_drag()) {
        // While a drag started by StartDragging is in flight, every mouse
        // move must also notify CEF as a drag-over so the renderer updates
        // the drop indicators and computes the eventual drop op.
        b->GetHost()->DragTargetDragOver(ev, handler->drag_allowed_ops());
    }
    b->GetHost()->SendMouseMoveEvent(ev, mouse_leave != 0);
}

extern "C" void excef_send_mouse_click(int browser_id, int x, int y,
                                       int button, int mouse_up,
                                       int click_count, int modifiers) {
    auto handler = exclr8cef::LookupOsrHandler(browser_id);
    if (!handler) return;
    auto b = handler->browser();
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    auto type = static_cast<cef_mouse_button_type_t>(button);

    // Left-button release while a drag is in flight: complete the drop.
    if (mouse_up != 0 && button == EXCEF_MBT_LEFT && handler->is_in_drag()) {
        auto host = b->GetHost();
        host->DragTargetDrop(ev);
        host->DragSourceEndedAt(x, y, handler->drag_current_op());
        host->DragSourceSystemDragEnded();
        handler->clear_drag();
        // Tell the host to drop the drag overlay (sentinel: zero-size image).
        if (exclr8cef::g_drag_image_cb)
            exclr8cef::g_drag_image_cb(handler->id(), nullptr, 0, 0, 0, 0);
    }

    b->GetHost()->SendMouseClickEvent(ev, type, mouse_up != 0, click_count);
}

extern "C" void excef_send_mouse_wheel(int browser_id, int x, int y,
                                       int delta_x, int delta_y,
                                       int modifiers) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->SendMouseWheelEvent(ev, delta_x, delta_y);
}

extern "C" void excef_send_key_event(int browser_id, int type,
                                     int windows_key_code, int native_key_code,
                                     int modifiers, int character,
                                     int unmodified_character,
                                     int is_system_key) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefKeyEvent ev;
    ev.type = static_cast<cef_key_event_type_t>(type);
    ev.modifiers = static_cast<uint32_t>(modifiers);
    ev.windows_key_code = windows_key_code;
    // Pass native_key_code through verbatim. DO NOT fall back to
    // windows_key_code on macOS: Carbon and Windows VK numbering disagree
    // on common keys (VK_TAB=0x09 collides with Carbon V=0x09, VK_V=0x56
    // collides with Carbon F5=0x60, etc.), so the fallback turns Tab
    // presses into V keypresses in Chromium's DOM event handler.
    ev.native_key_code = native_key_code;
    ev.is_system_key = is_system_key != 0;
    ev.character = static_cast<char16_t>(character);
    ev.unmodified_character = static_cast<char16_t>(unmodified_character);
    // CEF removed IsFocusOnEditableField in newer versions; matching
    // cefclient's macOS sample which hardcodes false here. Tab / Enter
    // still navigate because the renderer's focus controller detects
    // them via the character / windows_key_code combo.
    ev.focus_on_editable_field = false;
    b->GetHost()->SendKeyEvent(ev);
}

extern "C" void excef_set_browser_focus(int browser_id, int focus) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->SetFocus(focus != 0);
}

// ---- OSR low-level controls (all on CefBrowserHost) -----------------------

extern "C" void excef_invalidate(int browser_id, int type) {
    auto b = get_browser(browser_id);
    if (b) {
        auto et = type == 1 ? PET_POPUP : PET_VIEW;
        b->GetHost()->Invalidate(et);
    }
}

extern "C" void excef_send_capture_lost_event(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->SendCaptureLostEvent();
}

extern "C" void excef_print(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->Print();
}

extern "C" void excef_start_download(int browser_id, const char* url) {
    auto b = get_browser(browser_id);
    if (b && url && *url) b->GetHost()->StartDownload(url);
}

// NB: CEF 147 dropped CefBrowserHost::SetMouseCursorChangeDisabled /
// IsMouseCursorChangeDisabled. Kiosks wanting cursor-lock should
// intercept the CursorChanged event on CefBrowser and ignore (or set
// the host cursor back to whatever they want) — equivalent effect at
// the host-side rather than at the CefBrowserHost layer.

extern "C" void excef_set_windowless_frame_rate(int browser_id, int fps) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->SetWindowlessFrameRate(fps);
}

extern "C" int excef_get_windowless_frame_rate(int browser_id) {
    auto b = get_browser(browser_id);
    return b ? b->GetHost()->GetWindowlessFrameRate() : 0;
}

extern "C" void excef_send_touch_event(int browser_id,
                                        int id, float x, float y,
                                        float radius_x, float radius_y,
                                        float rotation_angle,
                                        float pressure,
                                        int type,
                                        int modifiers,
                                        int pointer_type) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefTouchEvent ev{};
    ev.id = id;
    ev.x = x;
    ev.y = y;
    ev.radius_x = radius_x;
    ev.radius_y = radius_y;
    ev.rotation_angle = rotation_angle;
    ev.pressure = pressure;
    ev.type = static_cast<cef_touch_event_type_t>(type);
    ev.modifiers = static_cast<uint32_t>(modifiers);
    ev.pointer_type = static_cast<cef_pointer_type_t>(pointer_type);
    b->GetHost()->SendTouchEvent(ev);
}

extern "C" void excef_send_external_begin_frame(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->SendExternalBeginFrame();
}

#if !defined(__APPLE__)
extern "C" int excef_start_external_begin_frame_clock(
    int,
    void*) {
    return 0;
}

extern "C" void excef_stop_external_begin_frame_clock(int) {}
#endif

namespace {

class CloseBrowserTask final : public CefTask {
public:
    CloseBrowserTask(int browser_id, bool force_close)
        : browser_id_(browser_id), force_close_(force_close) {}

    void Execute() override {
        auto browser = exclr8cef::GetOsrBrowser(browser_id_);
        if (browser) {
            browser->GetHost()->CloseBrowser(force_close_);
        }
    }

private:
    const int browser_id_;
    const bool force_close_;
    IMPLEMENT_REFCOUNTING(CloseBrowserTask);
};

}  // namespace

// ---- Navigation -----------------------------------------------------------

extern "C" void excef_load_url(int browser_id, const char* url) {
    auto b = get_browser(browser_id);
    if (!b || !url) return;
    b->GetMainFrame()->LoadURL(url);
}
extern "C" void excef_go_back(int browser_id) { auto b = get_browser(browser_id); if (b) b->GoBack(); }
extern "C" void excef_go_forward(int browser_id) { auto b = get_browser(browser_id); if (b) b->GoForward(); }
extern "C" void excef_reload(int browser_id, int ignore_cache) {
    auto b = get_browser(browser_id);
    if (!b) return;
    if (ignore_cache) b->ReloadIgnoreCache();
    else b->Reload();
}
extern "C" void excef_stop_load(int browser_id) { auto b = get_browser(browser_id); if (b) b->StopLoad(); }
extern "C" void excef_close_browser(int browser_id, int force_close) {
    if (!exclr8cef::LookupOsrHandler(browser_id)) return;

    // Managed load callbacks run synchronously inside CEF's TID_UI stack.
    // A host can legitimately release its view from LoadEnd; closing inline
    // there recursively re-enters Chromium teardown and can overflow the
    // native stack. Always queue close, even when the caller is already on
    // TID_UI, so the active CEF callback unwinds before destruction begins.
    CefPostTask(
        TID_UI,
        CefRefPtr<CefTask>(
            new CloseBrowserTask(browser_id, force_close != 0)));
}
extern "C" void excef_was_hidden(int browser_id, int hidden) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->WasHidden(hidden != 0);
}

// ---- JavaScript -----------------------------------------------------------

extern "C" void excef_execute_javascript(int browser_id, const char* code,
                                          const char* script_url) {
    auto b = get_browser(browser_id);
    if (!b || !code) return;
    b->GetMainFrame()->ExecuteJavaScript(code,
                                          script_url ? script_url : "", 0);
}

extern "C" void excef_set_eval_result_callback(excef_eval_result_cb_t cb) {
    exclr8cef::g_eval_result_cb = cb;
}

extern "C" int excef_eval_javascript(int browser_id, int request_id,
                                     const char* code) {
    auto b = get_browser(browser_id);
    if (!b || !code) return 0;
    auto msg = CefProcessMessage::Create("Eval");
    auto args = msg->GetArgumentList();
    args->SetInt(0, request_id);
    args->SetString(1, code);
    b->GetMainFrame()->SendProcessMessage(PID_RENDERER, msg);
    return 1;
}

// ---- DevTools -------------------------------------------------------------

// Default ShowDevTools — empty CefWindowInfo, lets CEF pick the host
// platform's default (separate OS window on every platform). The
// VibeCoder embedded-DevTools pane does NOT use this path; it spawns
// a second NativeWebView pointed at the loopback inspector URL
// (http://127.0.0.1:<rdp>/devtools/inspector.html?ws=…). Kept for
// callers that want the native separate-window DevTools.
extern "C" void excef_show_dev_tools(int browser_id) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefWindowInfo window_info;
    CefBrowserSettings settings;
    b->GetHost()->ShowDevTools(window_info, nullptr, settings, CefPoint());
}

extern "C" void excef_close_dev_tools(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->CloseDevTools();
}

// ---- Browser events -------------------------------------------------------

extern "C" void excef_set_address_change_callback(excef_address_change_cb_t cb) { exclr8cef::g_address_cb = cb; }
extern "C" void excef_set_title_change_callback(excef_title_change_cb_t cb) { exclr8cef::g_title_cb = cb; }
extern "C" void excef_set_loading_state_callback(excef_loading_state_cb_t cb) { exclr8cef::g_loading_state_cb = cb; }
extern "C" void excef_set_browser_closed_callback(excef_browser_closed_cb_t cb) { exclr8cef::g_browser_closed_cb = cb; }
extern "C" void excef_set_cursor_change_callback(excef_cursor_change_cb_t cb) { exclr8cef::g_cursor_change_cb = cb; }
extern "C" void excef_set_console_message_callback(excef_console_message_cb_t cb) { exclr8cef::g_console_message_cb = cb; }
extern "C" void excef_set_load_start_callback(excef_load_start_cb_t cb) { exclr8cef::g_load_start_cb = cb; }
extern "C" void excef_set_load_end_callback(excef_load_end_cb_t cb) { exclr8cef::g_load_end_cb = cb; }
extern "C" void excef_set_load_error_callback(excef_load_error_cb_t cb) { exclr8cef::g_load_error_cb = cb; }
extern "C" void excef_set_loading_progress_callback(excef_loading_progress_cb_t cb) { exclr8cef::g_loading_progress_cb = cb; }
extern "C" void excef_set_status_message_callback(excef_status_message_cb_t cb) { exclr8cef::g_status_message_cb = cb; }
extern "C" void excef_set_tooltip_callback(excef_tooltip_cb_t cb) { exclr8cef::g_tooltip_cb = cb; }
extern "C" void excef_set_favicon_callback(excef_favicon_cb_t cb) { exclr8cef::g_favicon_cb = cb; }
extern "C" void excef_set_fullscreen_callback(excef_fullscreen_cb_t cb) { exclr8cef::g_fullscreen_cb = cb; }
extern "C" void excef_set_browser_initialized_callback(excef_browser_initialized_cb_t cb) { exclr8cef::g_browser_initialized_cb = cb; }
extern "C" void excef_set_scroll_offset_callback(excef_scroll_offset_cb_t cb) { exclr8cef::g_scroll_offset_cb = cb; }
extern "C" void excef_set_auto_resize_callback(excef_auto_resize_cb_t cb) { exclr8cef::g_auto_resize_cb = cb; }
extern "C" void excef_set_js_dialog_callback(excef_js_dialog_cb_t cb) { exclr8cef::g_js_dialog_cb = cb; }
extern "C" void excef_set_file_dialog_callback(excef_file_dialog_cb_t cb) { exclr8cef::g_file_dialog_cb = cb; }
extern "C" void excef_set_context_menu_callback(excef_context_menu_cb_t cb) { exclr8cef::g_context_menu_cb = cb; }
extern "C" void excef_set_download_starting_callback(excef_download_starting_cb_t cb) { exclr8cef::g_download_starting_cb = cb; }
extern "C" void excef_set_download_progress_callback(excef_download_progress_cb_t cb) { exclr8cef::g_download_progress_cb = cb; }
extern "C" void excef_set_auth_request_callback(excef_auth_request_cb_t cb) { exclr8cef::g_auth_request_cb = cb; }
extern "C" void excef_set_find_result_callback(excef_find_result_cb_t cb) { exclr8cef::g_find_result_cb = cb; }
extern "C" void excef_set_render_process_gone_callback(excef_render_process_gone_cb_t cb) { exclr8cef::g_render_process_gone_cb = cb; }
extern "C" void excef_set_scheme_request_callback(excef_scheme_request_cb_t cb) { exclr8cef::g_scheme_request_cb = cb; }
extern "C" void excef_set_resource_request_callback(excef_resource_request_cb_t cb) { exclr8cef::g_resource_request_cb = cb; }
extern "C" void excef_set_before_browse_callback(excef_before_browse_cb_t cb) { exclr8cef::g_before_browse_cb = cb; }
extern "C" void excef_set_popup_show_callback(excef_popup_show_cb_t cb) { exclr8cef::g_popup_show_cb = cb; }
extern "C" void excef_set_popup_size_callback(excef_popup_size_cb_t cb) { exclr8cef::g_popup_size_cb = cb; }
extern "C" void excef_set_popup_paint_callback(excef_popup_paint_cb_t cb) { exclr8cef::g_popup_paint_cb = cb; }
extern "C" void excef_set_js_invoke_callback(excef_js_invoke_cb_t cb) { exclr8cef::g_js_invoke_cb = cb; }
extern "C" void excef_set_devtools_message_callback(excef_devtools_message_cb_t cb) {
    exclr8cef::g_devtools_message_cb = cb;
}
extern "C" void excef_set_string_visitor_callback(excef_string_visitor_cb_t cb) {
    exclr8cef::g_string_visitor_cb = cb;
}

namespace {
class StringRelay : public CefStringVisitor {
public:
    explicit StringRelay(int request_id) : request_id_(request_id) {}
    void Visit(const CefString& str) override {
        if (!exclr8cef::g_string_visitor_cb) return;
        std::string s = str.ToString();
        exclr8cef::g_string_visitor_cb(request_id_, s.c_str());
    }
private:
    int request_id_;
    IMPLEMENT_REFCOUNTING(StringRelay);
};
}  // namespace

extern "C" int excef_get_frame_source(int browser_id, int request_id) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return 0;
    auto frame = b->GetMainFrame();
    if (!frame) return 0;
    frame->GetSource(new StringRelay(request_id));
    return 1;
}

extern "C" int excef_get_frame_text(int browser_id, int request_id) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return 0;
    auto frame = b->GetMainFrame();
    if (!frame) return 0;
    frame->GetText(new StringRelay(request_id));
    return 1;
}

extern "C" void excef_set_nav_entry_callback(excef_nav_entry_cb_t cb) {
    exclr8cef::g_nav_entry_cb = cb;
}

extern "C" int excef_load_request(int browser_id,
                                    const char* method,
                                    const char* url,
                                    const unsigned char* post_body,
                                    int post_length,
                                    const char* headers_string) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b || !url) return 0;
    auto frame = b->GetMainFrame();
    if (!frame) return 0;
    CefRefPtr<CefRequest> req = CefRequest::Create();
    req->SetURL(url);
    if (method && *method) req->SetMethod(method);
    if (post_body && post_length > 0) {
        CefRefPtr<CefPostData> pd = CefPostData::Create();
        CefRefPtr<CefPostDataElement> el = CefPostDataElement::Create();
        el->SetToBytes(static_cast<size_t>(post_length), post_body);
        pd->AddElement(el);
        req->SetPostData(pd);
    }
    if (headers_string && *headers_string) {
        CefRequest::HeaderMap headers;
        const char* p = headers_string;
        while (*p) {
            const char* line_end = std::strchr(p, '\n');
            std::string line = line_end ? std::string(p, line_end - p) : std::string(p);
            auto colon = line.find(':');
            if (colon != std::string::npos) {
                std::string n = line.substr(0, colon);
                std::string v = line.substr(colon + 1);
                // trim leading space
                while (!v.empty() && (v.front() == ' ' || v.front() == '\t')) v.erase(v.begin());
                headers.insert({n, v});
            }
            if (!line_end) break;
            p = line_end + 1;
        }
        req->SetHeaderMap(headers);
    }
    frame->LoadRequest(req);
    return 1;
}

namespace {
class NavEntryRelay : public CefNavigationEntryVisitor {
public:
    explicit NavEntryRelay(int request_id) : request_id_(request_id) {}
    bool Visit(CefRefPtr<CefNavigationEntry> entry, bool current,
                int /*index*/, int total) override {
        if (!exclr8cef::g_nav_entry_cb) return false;
        std::string url = entry->GetURL().ToString();
        std::string display = entry->GetDisplayURL().ToString();
        std::string original = entry->GetOriginalURL().ToString();
        std::string title = entry->GetTitle().ToString();
        CefBaseTime t = entry->GetCompletionTime();
        // CefBaseTime.val is MICROSECONDS since the Windows epoch
        // (1601-01-01); convert to ms since the Unix epoch (1970-01-01).
        // The two epochs are 11644473600 seconds apart.
        constexpr long long kWindowsToUnixEpochUs = 11644473600LL * 1000000LL;
        long long ms = t.val > 0
            ? (static_cast<long long>(t.val) - kWindowsToUnixEpochUs) / 1000
            : 0;
        exclr8cef::g_nav_entry_cb(request_id_, /*done=*/0,
                                    current ? 1 : 0,
                                    url.c_str(), display.c_str(), original.c_str(),
                                    title.c_str(),
                                    static_cast<int>(entry->GetTransitionType()),
                                    entry->GetHttpStatusCode(),
                                    ms,
                                    entry->IsValid() ? 1 : 0);
        // Continue iterating; we mark done in a final call below in the
        // C ABI entry point after Visit returns.
        return true;
    }
    int id() const { return request_id_; }
private:
    int request_id_;
    IMPLEMENT_REFCOUNTING(NavEntryRelay);
};
}  // namespace

extern "C" int excef_get_navigation_entries(int browser_id, int request_id, int current_only) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return 0;
    CefRefPtr<NavEntryRelay> visitor = new NavEntryRelay(request_id);
    b->GetHost()->GetNavigationEntries(visitor, current_only != 0);
    // Send a final done marker so the host knows iteration is finished.
    // (CEF synchronously calls Visit() once per entry on TID_UI before
    // GetNavigationEntries returns, so this is safe to do here.)
    if (exclr8cef::g_nav_entry_cb) {
        exclr8cef::g_nav_entry_cb(request_id, 1, 0, nullptr, nullptr, nullptr, nullptr, 0, 0, 0, 0);
    }
    return 1;
}

extern "C" int excef_load_string(int browser_id, const char* html) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b || !html) return 0;
    auto frame = b->GetMainFrame();
    if (!frame) return 0;
    // CEF removed Frame::LoadString — load via data: URL with base64
    // encoding so arbitrary HTML (including special chars) survives the
    // URL trip. There's no way to forge a different origin from here;
    // callers that need a real-looking URL should register a custom
    // scheme handler and navigate to that instead.
    std::string url = "data:text/html;charset=utf-8;base64,";
    url += CefBase64Encode(html, strlen(html)).ToString();
    // Chromium hard-caps URLs at url::kMaxURLChars (2 MB) and silently
    // DROPS longer navigations at the IPC boundary — no LoadStart /
    // LoadEnd / LoadError ever fires. Documents that would blow the cap
    // are served through the internal resource handler instead (see
    // kLoadStringUrlPrefix): unlimited size, normal load events; the
    // trade-off is the location bar shows the synthetic https URL
    // rather than the data: URL.
    constexpr size_t kMaxUrlChars = 2u * 1024 * 1024;
    if (url.size() > kMaxUrlChars) {
        uint64_t token =
            exclr8cef::g_next_token.fetch_add(1, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lock(exclr8cef::g_loadstring_mu);
            exclr8cef::g_loadstring_docs[browser_id] =
                exclr8cef::PendingLoadStringDoc{token, std::string(html)};
        }
        frame->LoadURL(exclr8cef::kLoadStringUrlPrefix + std::to_string(token));
        return 1;
    }
    frame->LoadURL(url);
    return 1;
}

namespace {

// Classify a CDP message by its TOP-LEVEL keys only. Replies look like
// {"id":N,"result":...}; events like {"method":"...","params":...}. A
// whole-string substring search for "id": is wrong — many events carry a
// nested "id" key (Page.frameNavigated's frame.id, Runtime.execution-
// ContextCreated's context.id, …) and would be misrouted as replies,
// dropping the event and potentially resolving an unrelated pending
// request. This scanner walks the JSON once, string- and depth-aware,
// and only inspects keys of the root object — no allocation beyond the
// key scratch buffer, no full parse on the (potentially large,
// screencast-frame-sized) payload.
bool CdpMessageIsReply(const char* data, size_t size, int* out_id) {
    size_t i = 0;
    auto skip_ws = [&] {
        while (i < size && (data[i] == ' ' || data[i] == '\t' ||
                            data[i] == '\n' || data[i] == '\r')) ++i;
    };
    skip_ws();
    if (i >= size || data[i] != '{') return false;  // not an object → event
    ++i;
    int depth = 1;
    bool in_string = false, escape = false;
    std::string key;
    while (i < size) {
        char c = data[i];
        if (in_string) {
            if (escape) {
                escape = false;
            } else if (c == '\\') {
                escape = true;
            } else if (c == '"') {
                in_string = false;
                ++i;
                if (depth == 1) {
                    // Peek: a ':' next means `key` names a root-level member.
                    size_t j = i;
                    while (j < size && (data[j] == ' ' || data[j] == '\t' ||
                                        data[j] == '\n' || data[j] == '\r')) ++j;
                    if (j < size && data[j] == ':') {
                        if (key == "method") return false;  // event
                        if (key == "id") {
                            ++j;
                            // Same whitespace set as the pre-colon peek —
                            // \n/\r between ':' and the number is legal JSON.
                            while (j < size && (data[j] == ' ' || data[j] == '\t' ||
                                                data[j] == '\n' || data[j] == '\r')) ++j;
                            bool neg = false;
                            if (j < size && data[j] == '-') { neg = true; ++j; }
                            long long v = 0;
                            bool any = false;
                            while (j < size && data[j] >= '0' && data[j] <= '9') {
                                if (v < 1000000000LL)  // stop growing — ids are host ints
                                    v = v * 10 + (data[j] - '0');
                                ++j;
                                any = true;
                            }
                            if (v > 2147483647LL) v = 2147483647LL;  // clamp to int
                            if (any) {
                                *out_id = static_cast<int>(neg ? -v : v);
                                return true;  // reply
                            }
                            return false;  // "id" with non-numeric value
                        }
                    }
                }
                continue;
            } else if (depth == 1) {
                key += c;
            }
            ++i;
            continue;
        }
        switch (c) {
            case '"':
                in_string = true;
                if (depth == 1) key.clear();
                break;
            case '{':
            case '[':
                ++depth;
                break;
            case '}':
            case ']':
                if (--depth == 0) return false;  // root closed, no verdict → event
                break;
            default:
                break;
        }
        ++i;
    }
    return false;
}

class DevToolsObserver : public CefDevToolsMessageObserver {
public:
    explicit DevToolsObserver(int browser_id) : browser_id_(browser_id) {}
    bool OnDevToolsMessage(CefRefPtr<CefBrowser> /*browser*/,
                            const void* message, size_t message_size) override {
        if (!exclr8cef::g_devtools_message_cb) return false;
        std::string json(static_cast<const char*>(message), message_size);
        int message_id = 0;
        bool is_reply = CdpMessageIsReply(json.data(), json.size(), &message_id);
        exclr8cef::g_devtools_message_cb(browser_id_, is_reply ? 0 : 1, message_id, json.c_str());
        return false;  // don't suppress
    }
private:
    int browser_id_;
    IMPLEMENT_REFCOUNTING(DevToolsObserver);
};

void EnsureDevToolsObserver(int browser_id, CefRefPtr<CefBrowser> browser) {
    std::lock_guard<std::mutex> lock(exclr8cef::g_devtools_observers_mu);
    if (!exclr8cef::g_devtools_observers.count(browser_id)) {
        exclr8cef::g_devtools_observers[browser_id] =
            browser->GetHost()->AddDevToolsMessageObserver(
                new DevToolsObserver(browser_id));
    }
}

void ReportDevToolsExecutionFailure(int browser_id, int message_id) {
    if (!exclr8cef::g_devtools_message_cb) return;
    std::string json =
        "{\"id\":" + std::to_string(message_id)
        + ",\"error\":{\"code\":-32000,"
          "\"message\":\"CEF rejected the DevTools command.\"}}";
    exclr8cef::g_devtools_message_cb(
        browser_id,
        /*is_event=*/0,
        message_id,
        json.c_str());
}

int ExecuteDevToolsMethodOnUi(
        int browser_id,
        int message_id,
        const std::string& method,
        const std::string& params_json) {
    if (!CefCurrentlyOn(TID_UI)) {
        ReportDevToolsExecutionFailure(browser_id, message_id);
        return 0;
    }
    auto browser = exclr8cef::GetOsrBrowser(browser_id);
    if (!browser) {
        ReportDevToolsExecutionFailure(browser_id, message_id);
        return 0;
    }

    EnsureDevToolsObserver(browser_id, browser);
    CefRefPtr<CefDictionaryValue> params;
    if (!params_json.empty()) {
        auto value = CefParseJSON(params_json, JSON_PARSER_RFC);
        if (value && value->GetType() == VTYPE_DICTIONARY) {
            params = value->GetDictionary();
        }
    }
    int result = browser->GetHost()->ExecuteDevToolsMethod(
        message_id,
        method,
        params);
    if (result == 0) {
        ReportDevToolsExecutionFailure(browser_id, message_id);
    }
    return result;
}

class ExecuteDevToolsMethodTask final : public CefTask {
public:
    ExecuteDevToolsMethodTask(
            int browser_id,
            int message_id,
            std::string method,
            std::string params_json)
        : browser_id_(browser_id),
          message_id_(message_id),
          method_(std::move(method)),
          params_json_(std::move(params_json)) {}

    void Execute() override {
        ExecuteDevToolsMethodOnUi(
            browser_id_,
            message_id_,
            method_,
            params_json_);
    }

private:
    const int browser_id_;
    const int message_id_;
    const std::string method_;
    const std::string params_json_;
    IMPLEMENT_REFCOUNTING(ExecuteDevToolsMethodTask);
};

}  // namespace

extern "C" int excef_send_devtools_message(int browser_id,
                                            const char* message_json,
                                            int message_length) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b || !message_json || message_length <= 0) return 0;
    // Lazily register the observer on first send so the host's callback fires.
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_devtools_observers_mu);
        if (!exclr8cef::g_devtools_observers.count(browser_id)) {
            exclr8cef::g_devtools_observers[browser_id] = b->GetHost()->AddDevToolsMessageObserver(
                new DevToolsObserver(browser_id));
        }
    }
    return b->GetHost()->SendDevToolsMessage(message_json, static_cast<size_t>(message_length)) ? 1 : 0;
}

extern "C" int excef_execute_devtools_method(int browser_id,
                                              int message_id,
                                              const char* method,
                                              const char* params_json) {
    if (!method || message_id <= 0
        || !exclr8cef::LookupOsrHandler(browser_id)) {
        return 0;
    }

    std::string method_copy(method);
    std::string params_copy = params_json ? params_json : "";
    if (CefCurrentlyOn(TID_UI)) {
        return ExecuteDevToolsMethodOnUi(
            browser_id,
            message_id,
            method_copy,
            params_copy);
    }

    return CefPostTask(
        TID_UI,
        CefRefPtr<CefTask>(new ExecuteDevToolsMethodTask(
            browser_id,
            message_id,
            std::move(method_copy),
            std::move(params_copy)))) ? 1 : 0;
}
extern "C" void excef_set_take_focus_callback(excef_take_focus_cb_t cb) { exclr8cef::g_take_focus_cb = cb; }
extern "C" void excef_set_set_focus_callback(excef_set_focus_cb_t cb) { exclr8cef::g_set_focus_cb = cb; }
extern "C" void excef_set_got_focus_callback(excef_got_focus_cb_t cb) { exclr8cef::g_got_focus_cb = cb; }
extern "C" void excef_set_pre_key_callback(excef_pre_key_cb_t cb) { exclr8cef::g_pre_key_cb = cb; }
extern "C" void excef_set_key_event_callback(excef_key_event_cb_t cb) { exclr8cef::g_key_event_cb = cb; }

extern "C" void excef_resolve_js_invoke(uint64_t token, int success, const char* result_json) {
    exclr8cef::PendingJsInvoke entry;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_js_invoke_mu);
        auto it = exclr8cef::g_js_invoke_pending.find(token);
        if (it == exclr8cef::g_js_invoke_pending.end()) return;
        entry = it->second;
        exclr8cef::g_js_invoke_pending.erase(it);
    }
    if (!entry.frame) return;

    auto msg = CefProcessMessage::Create("JsInvokeReply");
    auto a = msg->GetArgumentList();
    a->SetInt(0, entry.request_id);
    a->SetBool(1, success != 0);
    a->SetString(2, result_json ? result_json : "null");
    entry.frame->SendProcessMessage(PID_RENDERER, msg);
}
extern "C" void excef_set_accessibility_tree_callback(excef_accessibility_tree_cb_t cb) { exclr8cef::g_a11y_tree_cb = cb; }
extern "C" void excef_set_accessibility_location_callback(excef_accessibility_location_cb_t cb) { exclr8cef::g_a11y_location_cb = cb; }

extern "C" void excef_set_accessibility_enabled(int browser_id, int enabled) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return;
    b->GetHost()->SetAccessibilityState(enabled ? STATE_ENABLED : STATE_DISABLED);
}

namespace {
class ResourceRequestResolveTask : public CefTask {
public:
    ResourceRequestResolveTask(CefRefPtr<CefCallback> cb, bool allow)
        : cb_(std::move(cb)), allow_(allow) {}
    void Execute() override {
        if (!cb_) return;
        if (allow_) cb_->Continue();
        else cb_->Cancel();
    }
private:
    CefRefPtr<CefCallback> cb_;
    bool allow_;
    IMPLEMENT_REFCOUNTING(ResourceRequestResolveTask);
};
}  // namespace

extern "C" void excef_resolve_resource_request(uint64_t token, int action, const char* new_headers) {
    CefRefPtr<CefRequest> request;
    CefRefPtr<CefCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_resource_request_mu);
        auto it = exclr8cef::g_resource_request_pending.find(token);
        if (it == exclr8cef::g_resource_request_pending.end()) return;
        request = it->second.request;
        callback = it->second.callback;
        exclr8cef::g_resource_request_pending.erase(it);
    }
    if (!callback) return;
    bool allow = (action == 0);
    if (allow && request && new_headers && *new_headers) {
        // Replace the entire header set with what the host provided.
        CefRequest::HeaderMap parsed;
        exclr8cef::ParseHeaders(new_headers, parsed);
        request->SetHeaderMap(parsed);
    }
    if (CefCurrentlyOn(TID_IO)) {
        if (allow) callback->Continue();
        else callback->Cancel();
    } else {
        CefPostTask(TID_IO, CefRefPtr<CefTask>(
            new ResourceRequestResolveTask(callback, allow)));
    }
}

extern "C" int excef_register_custom_scheme(const char* scheme_name,
                                              int is_standard,
                                              int is_local,
                                              int is_display_isolated,
                                              int is_secure,
                                              int is_cors_enabled,
                                              int is_csp_bypassing) {
    if (!scheme_name || !*scheme_name) return 1;
    int options = 0;
    if (is_standard)          options |= CEF_SCHEME_OPTION_STANDARD;
    if (is_local)             options |= CEF_SCHEME_OPTION_LOCAL;
    if (is_display_isolated)  options |= CEF_SCHEME_OPTION_DISPLAY_ISOLATED;
    if (is_secure)            options |= CEF_SCHEME_OPTION_SECURE;
    if (is_cors_enabled)      options |= CEF_SCHEME_OPTION_CORS_ENABLED;
    if (is_csp_bypassing)     options |= CEF_SCHEME_OPTION_CSP_BYPASSING;
    exclr8cef::AddCustomScheme(scheme_name, options);
    return 0;
}

extern "C" void excef_resolve_scheme_request(uint64_t token,
                                               int status_code,
                                               const char* status_text,
                                               const char* mime_type,
                                               const unsigned char* body,
                                               int body_length) {
    CefRefPtr<exclr8cef::SchemeResourceHandler> handler;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_scheme_mu);
        auto it = exclr8cef::g_scheme_pending.find(token);
        if (it == exclr8cef::g_scheme_pending.end()) return;
        handler = it->second.handler;
        exclr8cef::g_scheme_pending.erase(it);
    }
    if (!handler) return;
    std::string text = (status_text && *status_text) ? status_text : "";
    std::string mime = (mime_type && *mime_type) ? mime_type : "";
    std::vector<uint8_t> bytes;
    if (body && body_length > 0)
        bytes.assign(body, body + body_length);
    handler->Resolve(status_code, std::move(text), std::move(mime), std::move(bytes));
}

namespace exclr8cef {
// Called from Exclr8CefApp::OnContextInitialized once CEF is up. Registers
// our factory for every scheme the host registered before init.
void RegisterAllSchemeFactories() {
    auto names = GetRegisteredSchemeNames();
    if (names.empty()) return;
    CefRefPtr<SchemeFactory> factory = new SchemeFactory();
    for (const auto& name : names) {
        CefRegisterSchemeHandlerFactory(name, /*domain=*/CefString(), factory);
    }
}
}  // namespace exclr8cef

namespace {
class AuthResolveTask : public CefTask {
public:
    AuthResolveTask(CefRefPtr<CefAuthCallback> cb, bool ok, CefString user, CefString pass)
        : cb_(std::move(cb)), ok_(ok), user_(std::move(user)), pass_(std::move(pass)) {}
    void Execute() override {
        if (!cb_) return;
        if (ok_) cb_->Continue(user_, pass_);
        else cb_->Cancel();
    }
private:
    CefRefPtr<CefAuthCallback> cb_;
    bool ok_;
    CefString user_, pass_;
    IMPLEMENT_REFCOUNTING(AuthResolveTask);
};

class PermissionPromptResolveTask : public CefTask {
public:
    PermissionPromptResolveTask(CefRefPtr<CefPermissionPromptCallback> cb,
                                cef_permission_request_result_t result)
        : cb_(std::move(cb)), result_(result) {}
    void Execute() override { if (cb_) cb_->Continue(result_); }
private:
    CefRefPtr<CefPermissionPromptCallback> cb_;
    cef_permission_request_result_t result_;
    IMPLEMENT_REFCOUNTING(PermissionPromptResolveTask);
};

class MediaAccessResolveTask : public CefTask {
public:
    MediaAccessResolveTask(CefRefPtr<CefMediaAccessCallback> cb, uint32_t granted)
        : cb_(std::move(cb)), granted_(granted) {}
    void Execute() override {
        if (!cb_) return;
        if (granted_) cb_->Continue(granted_);
        else cb_->Cancel();
    }
private:
    CefRefPtr<CefMediaAccessCallback> cb_;
    uint32_t granted_;
    IMPLEMENT_REFCOUNTING(MediaAccessResolveTask);
};

class CertErrorResolveTask : public CefTask {
public:
    CertErrorResolveTask(CefRefPtr<CefCallback> cb, bool proceed)
        : cb_(std::move(cb)), proceed_(proceed) {}
    void Execute() override {
        if (!cb_) return;
        if (proceed_) cb_->Continue();
        else cb_->Cancel();
    }
private:
    CefRefPtr<CefCallback> cb_;
    bool proceed_;
    IMPLEMENT_REFCOUNTING(CertErrorResolveTask);
};
}  // namespace

extern "C" void excef_resolve_auth(uint64_t token, const char* username, const char* password) {
    CefRefPtr<CefAuthCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_auth_mu);
        auto it = exclr8cef::g_auth_pending.find(token);
        if (it == exclr8cef::g_auth_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_auth_pending.erase(it);
    }
    if (!callback) return;
    bool ok = username && *username;
    CefString u, p;
    if (ok) {
        u.FromString(username);
        if (password) p.FromString(password);
    }
    if (CefCurrentlyOn(TID_UI)) {
        if (ok) callback->Continue(u, p);
        else callback->Cancel();
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(new AuthResolveTask(callback, ok, u, p)));
    }
}

extern "C" void excef_set_permission_prompt_callback(excef_permission_prompt_cb_t cb) {
    exclr8cef::g_permission_prompt_cb = cb;
}

extern "C" void excef_resolve_permission_prompt(uint64_t token, int result) {
    CefRefPtr<CefPermissionPromptCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_permission_prompt_mu);
        auto it = exclr8cef::g_permission_prompt_pending.find(token);
        if (it == exclr8cef::g_permission_prompt_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_permission_prompt_pending.erase(it);
    }
    if (!callback) return;
    auto r = static_cast<cef_permission_request_result_t>(result);
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(r);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(new PermissionPromptResolveTask(callback, r)));
    }
}

extern "C" void excef_set_media_access_callback(excef_media_access_cb_t cb) {
    exclr8cef::g_media_access_cb = cb;
}

extern "C" void excef_set_before_popup_callback(excef_before_popup_cb_t cb) {
    exclr8cef::g_before_popup_cb = cb;
}

extern "C" void excef_set_cert_error_callback(excef_cert_error_cb_t cb) {
    exclr8cef::g_cert_error_cb = cb;
}

extern "C" void excef_resolve_cert_error(uint64_t token, int proceed) {
    CefRefPtr<CefCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_cert_error_mu);
        auto it = exclr8cef::g_cert_error_pending.find(token);
        if (it == exclr8cef::g_cert_error_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_cert_error_pending.erase(it);
    }
    if (!callback) return;
    bool ok = proceed != 0;
    if (CefCurrentlyOn(TID_UI)) {
        if (ok) callback->Continue();
        else callback->Cancel();
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(new CertErrorResolveTask(callback, ok)));
    }
}

extern "C" void excef_resolve_media_access(uint64_t token, int granted_permissions) {
    CefRefPtr<CefMediaAccessCallback> callback;
    uint32_t requested = 0;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_media_access_mu);
        auto it = exclr8cef::g_media_access_pending.find(token);
        if (it == exclr8cef::g_media_access_pending.end()) return;
        callback = it->second.callback;
        requested = it->second.requested;
        exclr8cef::g_media_access_pending.erase(it);
    }
    if (!callback) return;
    // Clamp granted to the set CEF actually requested — passing anything else
    // would trip CEF's DCHECK on the alloy callback.
    uint32_t granted = static_cast<uint32_t>(granted_permissions) & requested;
    if (CefCurrentlyOn(TID_UI)) {
        if (granted) callback->Continue(granted);
        else callback->Cancel();
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(new MediaAccessResolveTask(callback, granted)));
    }
}

extern "C" void excef_find(int browser_id, const char* search_text,
                            int forward, int match_case, int find_next) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return;
    CefString s;
    if (search_text) s.FromString(search_text);
    b->GetHost()->Find(s, forward != 0, match_case != 0, find_next != 0);
}

extern "C" void excef_stop_finding(int browser_id, int clear_selection) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return;
    b->GetHost()->StopFinding(clear_selection != 0);
}

namespace {
class DownloadStartingResolveTask : public CefTask {
public:
    DownloadStartingResolveTask(CefRefPtr<CefBeforeDownloadCallback> cb,
                                 CefString path, bool show_dialog)
        : cb_(std::move(cb)), path_(std::move(path)), show_(show_dialog) {}
    void Execute() override { if (cb_) cb_->Continue(path_, show_); }
private:
    CefRefPtr<CefBeforeDownloadCallback> cb_;
    CefString path_;
    bool show_;
    IMPLEMENT_REFCOUNTING(DownloadStartingResolveTask);
};
}  // namespace

extern "C" void excef_resolve_download_starting(uint64_t token,
                                                  const char* path,
                                                  int show_dialog) {
    CefRefPtr<CefBeforeDownloadCallback> callback;
    int browser_id = 0;
    uint32_t download_id = 0;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_download_starting_mu);
        auto it = exclr8cef::g_download_starting_pending.find(token);
        if (it == exclr8cef::g_download_starting_pending.end()) return;
        callback = it->second.callback;
        browser_id = it->second.browser_id;
        download_id = it->second.download_id;
        exclr8cef::g_download_starting_pending.erase(it);
    }
    if (!callback) return;
    if ((!path || !*path) && show_dialog == 0) {
        // Empty path + no dialog = CANCEL. Continue("") would NOT cancel —
        // per CEF it downloads to the suggested name in the default temp
        // directory. Instead mark the download cancelled and let
        // OnDownloadUpdated execute the actual
        // CefDownloadItemCallback::Cancel(); the unexecuted before-download
        // callback is simply dropped.
        //
        // Empty path + show_dialog falls through to Continue("", true)
        // below — CEF's "show the save-as dialog / you decide" signal,
        // which the managed no-subscriber default relies on.
        std::lock_guard<std::mutex> lock(exclr8cef::g_cancelled_downloads_mu);
        exclr8cef::g_cancelled_downloads.emplace(browser_id, download_id);
        return;
    }
    CefString p;
    if (path && *path) p.FromString(path);
    bool show = show_dialog != 0;
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(p, show);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new DownloadStartingResolveTask(callback, p, show)));
    }
}

// Download progress action. Called synchronously from the host's
// DownloadProgress event handler — outside that window the token is
// invalid (we erase right after returning from the host callback).
extern "C" void excef_download_action(uint64_t token, int action) {
    CefRefPtr<CefDownloadItemCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_download_progress_mu);
        auto it = exclr8cef::g_download_progress_pending.find(token);
        if (it == exclr8cef::g_download_progress_pending.end()) return;
        callback = it->second.callback;
    }
    if (!callback) return;
    // These can be called from any thread per CEF docs — no need to hop.
    switch (action) {
        case 0: callback->Cancel(); break;
        case 1: callback->Pause();  break;
        case 2: callback->Resume(); break;
    }
}

namespace {
// CefPostTask in this CEF version doesn't take naked lambdas via base::BindOnce
// without a function pointer, so we use explicit CefTask subclasses to hop
// to the CEF UI thread.
class JsDialogResolveTask : public CefTask {
public:
    JsDialogResolveTask(CefRefPtr<CefJSDialogCallback> cb, bool ok, CefString input)
        : cb_(std::move(cb)), ok_(ok), input_(std::move(input)) {}
    void Execute() override { if (cb_) cb_->Continue(ok_, input_); }
private:
    CefRefPtr<CefJSDialogCallback> cb_;
    bool ok_;
    CefString input_;
    IMPLEMENT_REFCOUNTING(JsDialogResolveTask);
};

class FileDialogResolveTask : public CefTask {
public:
    FileDialogResolveTask(CefRefPtr<CefFileDialogCallback> cb, std::vector<CefString> paths)
        : cb_(std::move(cb)), paths_(std::move(paths)) {}
    void Execute() override { if (cb_) cb_->Continue(paths_); }
private:
    CefRefPtr<CefFileDialogCallback> cb_;
    std::vector<CefString> paths_;
    IMPLEMENT_REFCOUNTING(FileDialogResolveTask);
};

void CompleteContextMenu(
        CefRefPtr<CefRunContextMenuCallback> callback,
        int browser_id,
        const std::string& link_url,
        int command_id) {
    if (!callback) return;
    if (command_id == exclr8cef::kOpenLinkInNewTabCommandId) {
        callback->Cancel();
        if (exclr8cef::g_before_popup_cb && !link_url.empty()) {
            exclr8cef::g_before_popup_cb(
                browser_id,
                link_url.c_str(),
                "",
                CEF_WOD_NEW_FOREGROUND_TAB,
                1);
        }
        return;
    }

    if (command_id < 0) {
        callback->Cancel();
        return;
    }

    callback->Continue(command_id, EVENTFLAG_NONE);
}

class ContextMenuResolveTask : public CefTask {
public:
    ContextMenuResolveTask(
            CefRefPtr<CefRunContextMenuCallback> cb,
            int browser_id,
            std::string link_url,
            int command_id)
        : cb_(std::move(cb)),
          browser_id_(browser_id),
          link_url_(std::move(link_url)),
          command_id_(command_id) {}
    void Execute() override {
        CompleteContextMenu(
            cb_, browser_id_, link_url_, command_id_);
    }
private:
    CefRefPtr<CefRunContextMenuCallback> cb_;
    int browser_id_;
    std::string link_url_;
    int command_id_;
    IMPLEMENT_REFCOUNTING(ContextMenuResolveTask);
};
}  // namespace

// Resolve a pending JS dialog. Look the token up, invoke Continue on the
// CEF UI thread (where the callback expects to be called from), drop our
// refcount. No-op on unknown tokens — safe for double-resolve / post-close.
// Resolve a pending file dialog. `paths` is newline-separated absolute
// paths (empty / NULL = cancel). Look up token, hop to UI thread, invoke
// CefFileDialogCallback::Continue. No-op on unknown token.
extern "C" void excef_resolve_file_dialog(uint64_t token, const char* paths) {
    CefRefPtr<CefFileDialogCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_file_dialog_mu);
        auto it = exclr8cef::g_file_dialog_pending.find(token);
        if (it == exclr8cef::g_file_dialog_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_file_dialog_pending.erase(it);
    }
    if (!callback) return;

    std::vector<CefString> selected;
    if (paths && *paths) {
        std::string s(paths);
        size_t start = 0;
        for (size_t i = 0; i <= s.size(); ++i) {
            if (i == s.size() || s[i] == '\n') {
                if (i > start) {
                    CefString p;
                    p.FromString(s.substr(start, i - start));
                    selected.push_back(p);
                }
                start = i + 1;
            }
        }
    }
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(selected);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new FileDialogResolveTask(callback, std::move(selected))));
    }
}

// Resolve a pending context menu. -1 = cancel; otherwise the chosen
// command id (must be one of the items we surfaced via the menu callback).
extern "C" void excef_resolve_context_menu(uint64_t token, int command_id) {
    exclr8cef::PendingContextMenu pending{};
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_context_menu_mu);
        auto it = exclr8cef::g_context_menu_pending.find(token);
        if (it == exclr8cef::g_context_menu_pending.end()) return;
        pending = std::move(it->second);
        exclr8cef::g_context_menu_pending.erase(it);
    }
    if (!pending.callback) return;
    if (CefCurrentlyOn(TID_UI)) {
        CompleteContextMenu(
            pending.callback,
            pending.browser_id,
            pending.link_url,
            command_id);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new ContextMenuResolveTask(
                pending.callback,
                pending.browser_id,
                std::move(pending.link_url),
                command_id)));
    }
}

extern "C" void excef_resolve_js_dialog(uint64_t token, int success, const char* user_input) {
    CefRefPtr<CefJSDialogCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_js_dialog_mu);
        auto it = exclr8cef::g_js_dialog_pending.find(token);
        if (it == exclr8cef::g_js_dialog_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_js_dialog_pending.erase(it);
    }
    if (!callback) return;
    CefString input;
    if (user_input && *user_input) input.FromString(user_input);
    bool ok = success != 0;
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(ok, input);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new JsDialogResolveTask(callback, ok, input)));
    }
}

extern "C" int excef_can_zoom(int browser_id, int command) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return 0;
    return b->GetHost()->CanZoom(static_cast<cef_zoom_command_t>(command)) ? 1 : 0;
}

extern "C" void excef_set_auto_resize_enabled(int browser_id, int enabled,
                                                int min_w, int min_h,
                                                int max_w, int max_h) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return;
    if (enabled) {
        b->GetHost()->SetAutoResizeEnabled(true,
            CefSize(min_w, min_h), CefSize(max_w, max_h));
    } else {
        b->GetHost()->SetAutoResizeEnabled(false, CefSize(), CefSize());
    }
}

extern "C" void excef_exit_fullscreen(int browser_id, int will_cause_resize) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (b) b->GetHost()->ExitFullscreen(will_cause_resize != 0);
}

// ---- Cookies --------------------------------------------------------------

namespace {

class CookieVisitorImpl : public CefCookieVisitor {
public:
    CookieVisitorImpl(int request_id) : request_id_(request_id) {}

    bool Visit(const CefCookie& cookie, int count, int total,
               bool& /*deleteCookie*/) override {
        if (exclr8cef::g_cookie_visit_cb) {
            std::string name = CefString(&cookie.name).ToString();
            std::string value = CefString(&cookie.value).ToString();
            std::string domain = CefString(&cookie.domain).ToString();
            std::string path = CefString(&cookie.path).ToString();
            exclr8cef::g_cookie_visit_cb(
                request_id_, /*done=*/0,
                name.c_str(), value.c_str(),
                domain.c_str(), path.c_str(),
                cookie.secure ? 1 : 0,
                cookie.httponly ? 1 : 0);
            if (count == total - 1) {
                fired_done_ = true;  // suppress the destructor's fallback done
                exclr8cef::g_cookie_visit_cb(
                    request_id_, /*done=*/1,
                    "", "", "", "", 0, 0);
            }
        }
        return true;
    }

    ~CookieVisitorImpl() override {
        // Total may have been 0 — fire the done call so the host's TCS can complete.
        if (!fired_done_ && exclr8cef::g_cookie_visit_cb) {
            exclr8cef::g_cookie_visit_cb(
                request_id_, /*done=*/1,
                "", "", "", "", 0, 0);
        }
    }

private:
    int request_id_;
    bool fired_done_ = false;
    IMPLEMENT_REFCOUNTING(CookieVisitorImpl);
};

}  // namespace

extern "C" void excef_set_cookie_visit_callback(excef_cookie_visit_cb_t cb) {
    exclr8cef::g_cookie_visit_cb = cb;
}

namespace {
// Resolve a cookie manager from a context handle. handle=0 → global.
CefRefPtr<CefCookieManager> CookieManagerForContext(int context_handle) {
    if (context_handle == 0)
        return CefCookieManager::GetGlobalManager(nullptr);
    auto ctx = exclr8cef::ResolveContext(context_handle);
    return ctx ? ctx->GetCookieManager(nullptr) : nullptr;
}
}  // namespace

extern "C" int excef_get_cookies_in_context(int context_handle, const char* url, int request_id) {
    auto mgr = CookieManagerForContext(context_handle);
    if (!mgr) return 0;
    CefRefPtr<CookieVisitorImpl> visitor(new CookieVisitorImpl(request_id));
    if (url && *url) {
        mgr->VisitUrlCookies(url, /*include_http_only=*/true, visitor);
    } else {
        mgr->VisitAllCookies(visitor);
    }
    return request_id;
}

extern "C" int excef_set_cookie_in_context(int context_handle, const char* url,
                                            const char* name, const char* value,
                                            const char* domain, const char* path,
                                            int secure, int httponly) {
    if (!url || !name) return 0;
    auto mgr = CookieManagerForContext(context_handle);
    if (!mgr) return 0;
    CefCookie c;
    // The managed side hands us UTF-8 — FromASCII would mangle any
    // non-ASCII cookie value (common in the wild) byte-by-byte.
    CefString(&c.name).FromString(name);
    if (value) CefString(&c.value).FromString(value);
    if (domain) CefString(&c.domain).FromString(domain);
    if (path) CefString(&c.path).FromString(path);
    c.secure = secure != 0;
    c.httponly = httponly != 0;
    return mgr->SetCookie(url, c, nullptr) ? 1 : 0;
}

extern "C" void excef_delete_cookies_in_context(int context_handle, const char* url, const char* name) {
    auto mgr = CookieManagerForContext(context_handle);
    if (!mgr) return;
    mgr->DeleteCookies(url ? url : "", name ? name : "", nullptr);
}

extern "C" int excef_delete_cookies_in_context_async(
    int context_handle, const char* url, const char* name, int request_id) {
    auto mgr = CookieManagerForContext(context_handle);
    if (!mgr) return 0;
    return mgr->DeleteCookies(
        url ? url : "",
        name ? name : "",
        new CookieDeletionRelay(request_id)) ? 1 : 0;
}

extern "C" int excef_flush_cookie_store_async(
    int context_handle, int request_id) {
    auto mgr = CookieManagerForContext(context_handle);
    if (!mgr) return 0;
    return mgr->FlushStore(new RequestContextCompletionRelay(request_id)) ? 1 : 0;
}

extern "C" int excef_get_cookies(const char* url, int request_id) {
    return excef_get_cookies_in_context(0, url, request_id);
}

extern "C" int excef_set_cookie(const char* url,
                                const char* name,
                                const char* value,
                                const char* domain,
                                const char* path,
                                int secure,
                                int httponly) {
    return excef_set_cookie_in_context(0, url, name, value, domain, path, secure, httponly);
}

extern "C" void excef_delete_cookies(const char* url, const char* name) {
    excef_delete_cookies_in_context(0, url, name);
}

// ---- Drag and drop --------------------------------------------------------
//
// The drag-target side (`_drag_target_*`) lets the host forward OS-level
// drag events from the platform (Avalonia, WPF, Cocoa) into CEF so the page
// sees real drag-over / drop interactions.
//
// The drag-source side (`_drag_source_*` + start-drag callback) lets the
// host learn when CEF wants to start a drag (`<a draggable>`, file from
// `<input type=file>`, etc.) and decide whether to launch an OS-level drag.

extern "C" void excef_drag_target_drag_enter(int browser_id,
                                              int x, int y,
                                              int modifiers,
                                              int allowed_ops,
                                              const char* text,
                                              const char* html,
                                              const char* url,
                                              const char** file_paths,
                                              int file_path_count) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefRefPtr<CefDragData> data = CefDragData::Create();
    if (text && *text) data->SetFragmentText(text);
    if (html && *html) data->SetFragmentHtml(html);
    if (url && *url) data->SetLinkURL(url);
    if (file_paths && file_path_count > 0) {
        for (int i = 0; i < file_path_count; ++i) {
            const char* p = file_paths[i];
            if (!p) continue;
            // Display name = the basename; full path = the full string.
            std::string s(p);
            auto slash = s.find_last_of("/\\");
            std::string base = (slash == std::string::npos) ? s : s.substr(slash + 1);
            data->AddFile(p, base);
        }
    }
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->DragTargetDragEnter(
        data, ev,
        static_cast<cef_drag_operations_mask_t>(allowed_ops));
}

extern "C" void excef_drag_target_drag_over(int browser_id,
                                              int x, int y,
                                              int modifiers,
                                              int allowed_ops) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->DragTargetDragOver(
        ev, static_cast<cef_drag_operations_mask_t>(allowed_ops));
}

extern "C" void excef_drag_target_drop(int browser_id,
                                        int x, int y,
                                        int modifiers) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->DragTargetDrop(ev);
}

extern "C" void excef_drag_target_drag_leave(int browser_id) {
    auto b = get_browser(browser_id);
    if (!b) return;
    b->GetHost()->DragTargetDragLeave();
}

extern "C" void excef_set_start_drag_callback(excef_start_drag_cb_t cb) {
    exclr8cef::g_start_drag_cb = cb;
}

extern "C" void excef_set_drag_image_callback(excef_drag_image_cb_t cb) {
    exclr8cef::g_drag_image_cb = cb;
}

extern "C" void excef_drag_source_ended_at(int browser_id,
                                             int x, int y, int op) {
    auto b = get_browser(browser_id);
    if (!b) return;
    b->GetHost()->DragSourceEndedAt(
        x, y, static_cast<cef_drag_operations_mask_t>(op));
}

extern "C" void excef_drag_source_system_drag_ended(int browser_id) {
    auto handler = exclr8cef::LookupOsrHandler(browser_id);
    if (!handler) return;
    auto b = handler->browser();
    if (!b) return;
    b->GetHost()->DragSourceSystemDragEnded();
    handler->clear_drag();
}

// ---- IME ------------------------------------------------------------------

extern "C" void excef_ime_set_composition(int browser_id,
                                           const char* text,
                                           int replacement_range_from,
                                           int replacement_range_length,
                                           int selection_range_from,
                                           int selection_range_length) {
    auto b = get_browser(browser_id);
    if (!b || !text) return;
    std::vector<CefCompositionUnderline> underlines;
    CefRange replacement(replacement_range_from,
                         replacement_range_from + replacement_range_length);
    CefRange selection(selection_range_from,
                       selection_range_from + selection_range_length);
    b->GetHost()->ImeSetComposition(text, underlines, replacement, selection);
}

extern "C" void excef_ime_commit_text(int browser_id,
                                      const char* text,
                                      int replacement_range_from,
                                      int replacement_range_length,
                                      int relative_cursor_pos) {
    auto b = get_browser(browser_id);
    if (!b || !text) return;
    CefRange replacement(replacement_range_from,
                         replacement_range_from + replacement_range_length);
    b->GetHost()->ImeCommitText(text, replacement, relative_cursor_pos);
}

extern "C" void excef_ime_finish_composing(int browser_id, int keep_selection) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->ImeFinishComposingText(keep_selection != 0);
}

extern "C" void excef_ime_cancel(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->ImeCancelComposition();
}

// ---- PrintToPDF -----------------------------------------------------------

namespace {

class PdfCallback : public CefPdfPrintCallback {
public:
    PdfCallback(int browser_id, excef_pdf_done_callback_t cb)
        : browser_id_(browser_id), cb_(cb) {}

    void OnPdfPrintFinished(const CefString& /*path*/, bool ok) override {
        if (cb_) cb_(browser_id_, ok ? 1 : 0);
    }

private:
    int browser_id_;
    excef_pdf_done_callback_t cb_;
    IMPLEMENT_REFCOUNTING(PdfCallback);
};

}  // namespace

extern "C" int excef_print_to_pdf(int browser_id, const char* path,
                                  excef_pdf_done_callback_t callback) {
    if (!path) return 0;
    auto browser = exclr8cef::GetOsrBrowser(browser_id);
    if (!browser) return 0;
    CefPdfPrintSettings settings;
    browser->GetHost()->PrintToPDF(path, settings,
                                   new PdfCallback(browser_id, callback));
    return 1;
}
