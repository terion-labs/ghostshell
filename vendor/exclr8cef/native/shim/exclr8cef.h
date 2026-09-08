// Exclr8CEF — public C ABI for the Chromium binding.
// This header is what ClangSharp parses to generate C# P/Invoke bindings,
// so keep it strict C — no C++ types crossing the boundary.

#ifndef EXCLR8CEF_H_
#define EXCLR8CEF_H_

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  ifdef EXCLR8CEF_BUILDING
#    define EXCEF_API __declspec(dllexport)
#  else
#    define EXCEF_API __declspec(dllimport)
#  endif
#else
#  define EXCEF_API __attribute__((visibility("default")))
#endif

// ---- Versions -------------------------------------------------------------

#define EXCEF_VERSION_BUFFER_SIZE 64

typedef struct excef_versions {
    char shim_version[EXCEF_VERSION_BUFFER_SIZE];
    char cef_version[EXCEF_VERSION_BUFFER_SIZE];
    char chromium_version[EXCEF_VERSION_BUFFER_SIZE];
} excef_versions;

EXCEF_API void excef_get_versions(excef_versions* out);

// ---- Process / lifecycle --------------------------------------------------

// Returns:
//   >=  0  → caller is a CEF subprocess; exit with this code immediately
//   -1     → caller is the main process; continue with excef_initialize
EXCEF_API int excef_execute_process(int argc, char** argv);

// Initialize CEF in the main process. Sets up NSApplication on macOS.
//   subprocess_path: path to the helper executable. May be NULL on macOS,
//                    where CEF auto-discovers the Helper.app inside the
//                    bundle.
EXCEF_API int excef_initialize(int argc, char** argv, const char* subprocess_path);

// Open a top-level browser window pointed at `url`. Safe to call before or
// after excef_initialize; URLs requested before init complete are queued
// and opened automatically when CEF is ready.
EXCEF_API int excef_create_browser(const char* url);

// Run CEF's message loop. Blocks until all browsers close or
// excef_quit_message_loop is called.
EXCEF_API void excef_run_message_loop(void);

// Request that the message loop exit. Safe to call from any thread.
EXCEF_API void excef_quit_message_loop(void);

// Shut down CEF. Call after excef_run_message_loop returns.
EXCEF_API void excef_shutdown(void);

// ---- External message pump (for hosts that own their own UI loop) --------
//
// Use this initialization variant when the host (e.g. Avalonia, WPF)
// already runs the platform message loop and CEF needs to cooperate.
// Sets CefSettings.external_message_pump = true and registers a callback
// that CEF calls to ask the host to schedule pump work.

// Schedule a call to excef_do_message_loop_work after `delay_ms` ms.
// Called from CEF's internal threads — the host should marshal back to its
// UI thread and call excef_do_message_loop_work when the delay elapses.
typedef void (*excef_schedule_pump_work_t)(long long delay_ms);

EXCEF_API int excef_initialize_external_pump(int argc, char** argv,
                                             const char* subprocess_path,
                                             excef_schedule_pump_work_t schedule_callback);

// Drive CEF's pending work. Call from the host UI thread when the
// scheduled callback's delay has elapsed.
EXCEF_API void excef_do_message_loop_work(void);

// ---- Embedded browser ----------------------------------------------------
//
// Create an NSView (macOS) / HWND (Windows) / X11 Window (Linux) that
// hosts a CEF browser, sized to width x height, navigated to `url`.
// The caller (UI framework) takes ownership of the returned native handle:
//   - On macOS the NSView is returned with +1 retain; AppKit/ARC consumes it.
// The CEF browser is created asynchronously and added as a subview of the
// returned host view.
// Returns the host NSView and writes the browser id to *out_browser_id
// (writes 0 on failure). The browser id lets the host wire up the per-
// browser event surface (load / console / drag / permission / …) the
// same way CreateOffscreenBrowser does.
EXCEF_API void* excef_create_browser_view(int width, int height,
                                          const char* url,
                                          int* out_browser_id);

// Two-phase embedded-browser creation, recommended over the all-in-one
// excef_create_browser_view when hosting inside a UI framework that
// parents the returned NSView after the call (Avalonia NativeControlHost,
// WPF HwndHost-equivalent, etc).
//
// Phase 1: create an empty host NSView. The host returns this to the UI
// framework, which parents it into the window hierarchy.
EXCEF_API void* excef_create_embedded_host(int width, int height);
// Parent-aware variant of the above. REQUIRED on Windows — a WS_CHILD
// HWND cannot be created without a parent (the parentless form falls back
// to HWND_MESSAGE there). On macOS the parent is ignored. Pass the handle
// the UI framework provides (e.g. Avalonia's CreateNativeControlCore
// parent).
EXCEF_API void* excef_create_embedded_host_in_parent(void* parent,
                                                     int width, int height);
// Release the host widget created by excef_create_embedded_host[_in_parent]
// (balances the retain on macOS / destroys the HWND on Windows). Safe to
// call while the attached browser is still closing — actual destruction is
// deferred until the browser's OnBeforeClose in that case. Call after
// requesting browser close, once the UI framework has detached the widget.
EXCEF_API void excef_destroy_embedded_host(void* host_view);
// Phase 2: with the host now parented to its window, attach a CEF browser
// to it. Chromium's renderer reads backingScaleFactor at CreateBrowser
// time — calling Phase 2 after parenting fixes initial DSF detection on
// Retina displays. Returns the new browser id (>0) or 0 on failure.
EXCEF_API int excef_attach_embedded_browser(void* host_view, int width, int height,
                                             const char* url);

// Request-context-aware variants — same as the two functions above but
// the browser is created inside the named CefRequestContext for cookie/
// cache/storage isolation. context_handle = 0 → global context (same as
// the non-_in_context variants). Mirrors the OSR side's
// excef_create_offscreen_browser_in_context.
EXCEF_API void* excef_create_browser_view_in_context(int width, int height,
                                                       const char* url,
                                                       int* out_browser_id,
                                                       int context_handle);
EXCEF_API int excef_attach_embedded_browser_in_context(void* host_view,
                                                         int width, int height,
                                                         const char* url,
                                                         int context_handle);

// V2: like _in_context but accepts an ARGB background colour Chromium
// uses for the render target before any HTML paints. CEF's default
// (when 0 is passed) is opaque white, which appears as a "white
// flash" on the embedded NSView/HWND between attach and first page
// paint. Alpha must be 0x00 (transparent) or 0xFF (opaque).
EXCEF_API int excef_attach_embedded_browser_in_context_v2(void* host_view,
                                                            int width, int height,
                                                            const char* url,
                                                            int context_handle,
                                                            uint32_t background_color);

// Resize a browser view previously returned by excef_create_browser_view.
// Walks the host view's direct subviews and resizes them so the embedded
// Chromium browser tracks the host's layout. Call on every layout change
// from the UI framework. macOS-only for v0.
EXCEF_API void excef_resize_browser_view(void* host_view, int width, int height);

// Hide / show the host NSView (mac) / HWND (win) in place WITHOUT
// destroying the browser. Use this to suppress an embedded browser
// from painting over sibling host content (e.g. a tab-switch path
// where the previous tab's NSView still draws on top of the new
// foreground tab — Avalonia's ZIndex / Opacity don't reorder native
// children). Also calls CefBrowserHost::WasHidden so Chromium can
// throttle rendering work.
EXCEF_API void excef_set_embedded_host_hidden(void* host_view, int hidden);

// ---- Off-screen rendering (OSR) ------------------------------------------
//
// OSR mode: CEF doesn't create any native windows. Instead it renders to
// an internal pixel buffer and notifies the host via a paint callback,
// which the host can blit onto its own UI surface (Skia, GDI, etc.).
// This sidesteps macOS NSView/NSApplication conflicts when CEF is hosted
// inside an existing UI framework like Avalonia.

// Paint callback. `buffer` is BGRA8888 (32-bit, top-left origin) and is
// only valid during the call — copy what you need. Stride = width * 4.
typedef void (*excef_paint_callback_t)(
    int browser_id,
    const void* buffer,
    int width,
    int height);

// Initialize CEF for off-screen rendering with an external message pump.
// Combines external_message_pump=true and windowless_rendering_enabled=true.
EXCEF_API int excef_initialize_offscreen(
    int argc, char** argv,
    const char* subprocess_path,
    excef_schedule_pump_work_t schedule_callback);

// Create an off-screen browser whose view rect is `width x height` DIPs (CSS
// pixels), navigated to `url`. `device_scale_factor` controls HiDPI rendering:
// CEF allocates a paint buffer of (width × scale) × (height × scale) physical
// pixels and the page is laid out at the DIP size. Pass 1.0 for non-HiDPI hosts.
// `paint` fires whenever CEF has new pixels — the (width, height) reported to
// `paint` are in physical pixels.
// Returns a browser id (>= 1) on success, 0 on failure.
EXCEF_API int excef_create_offscreen_browser(
    int width, int height,
    float device_scale_factor,
    const char* url,
    excef_paint_callback_t paint);

// Tell CEF the browser has been resized to `width x height` DIPs and request
// a fresh paint. The buffer size will follow the current device_scale_factor.
EXCEF_API void excef_resize_offscreen_browser(int browser_id,
                                              int width, int height);

// Update the browser's device scale factor (e.g. when the host control is
// dragged across monitors with different DPI). CEF will re-layout and emit a
// fresh paint at the new physical-pixel size.
EXCEF_API void excef_set_device_scale_factor(int browser_id, float scale);

// Set the zoom level. 0.0 == 100% (default). Each 1.0 step is ~120% of the
// previous (CEF/Chromium convention: percentage = 100 * pow(1.2, level)).
EXCEF_API void excef_set_zoom_level(int browser_id, double level);
EXCEF_API double excef_get_zoom_level(int browser_id);

// Notify Chromium that the browser's hosting window has started moving
// or resizing (IME composition window placement, perf hints).
EXCEF_API void excef_notify_move_or_resize_started(int browser_id);
// Notify Chromium the screen info changed (DPI / display change after
// drag between monitors). Same call as SetDeviceScaleFactor + a generic
// re-query trigger.
EXCEF_API void excef_notify_screen_info_changed(int browser_id);

// Spellcheck (right-click "Add to Dictionary" / "Replace") wired into
// CefBrowserHost. The misspelled-word + suggestions come from
// CefContextMenuParams in the ContextMenu event.
EXCEF_API void excef_replace_misspelling(int browser_id, const char* word);
EXCEF_API void excef_add_word_to_dictionary(int browser_id, const char* word);

// ---- Frame handler (iframe lifecycle) ---------------------------------
//
// CefFrameHandler. Fires for every frame in the browser — main + iframes.
// Useful for automation that needs to track when iframes appear /
// disappear, and for cross-origin frame inventory.
//
// `frame_id` is a stable identifier (CefFrame::GetIdentifier — string
// in CEF 124+).

typedef void (*excef_frame_lifecycle_cb_t)(int browser_id,
                                             int event_type,  // 0=created, 1=attached, 2=detached
                                             const char* frame_id,
                                             const char* parent_frame_id,
                                             const char* name,
                                             const char* url,
                                             int is_main);
EXCEF_API void excef_set_frame_lifecycle_callback(excef_frame_lifecycle_cb_t cb);

typedef void (*excef_main_frame_changed_cb_t)(int browser_id,
                                                 const char* old_frame_id,
                                                 const char* new_frame_id);
EXCEF_API void excef_set_main_frame_changed_callback(excef_main_frame_changed_cb_t cb);

// ---- Audio capture (CefAudioHandler) -----------------------------------
//
// Captures the tab's audio output. The shim interleaves the planar float
// PCM CEF emits into a single buffer per packet so the host doesn't have
// to dereference the channel pointer array across the FFI boundary.
//
// Audio capture is OFF by default; calling `excef_enable_audio_capture(id, 1)`
// installs the handler. CEF won't emit packets until at least one media
// element is playing.

EXCEF_API void excef_enable_audio_capture(int browser_id, int enable);

typedef void (*excef_audio_stream_started_cb_t)(int browser_id,
                                                  int channel_layout,
                                                  int sample_rate,
                                                  int frames_per_buffer,
                                                  int channels);
EXCEF_API void excef_set_audio_stream_started_callback(excef_audio_stream_started_cb_t cb);

// `interleaved` is a buffer of `frames * channels` float samples in CHANNEL
// order (LRLR…). Pointer is only valid for the duration of the callback —
// copy if you need to retain. `pts` is microseconds since stream start.
typedef void (*excef_audio_stream_packet_cb_t)(int browser_id,
                                                 const float* interleaved,
                                                 int frames,
                                                 int channels,
                                                 int64_t pts_us);
EXCEF_API void excef_set_audio_stream_packet_callback(excef_audio_stream_packet_cb_t cb);

typedef void (*excef_audio_stream_stopped_cb_t)(int browser_id);
EXCEF_API void excef_set_audio_stream_stopped_callback(excef_audio_stream_stopped_cb_t cb);

typedef void (*excef_audio_stream_error_cb_t)(int browser_id, const char* message);
EXCEF_API void excef_set_audio_stream_error_callback(excef_audio_stream_error_cb_t cb);

// Mute / unmute the browser's audio output. Mute state is per-browser.
EXCEF_API void excef_set_audio_muted(int browser_id, int muted);
EXCEF_API int excef_is_audio_muted(int browser_id);

// ---- Response filter (body rewrite) ------------------------------------
//
// CefResourceRequestHandler::GetResourceResponseFilter — lets the host
// rewrite response bodies as they stream past, before the renderer parses
// them. Useful for HTML/JS injection, CSP stripping, content-type fixups.
//
// Per-response token model: should_filter is called once per response;
// returning 1 installs a filter and CEF then calls back into excef_response_filter_cb_t
// repeatedly with (data_in, data_out) chunks until the body is done.
// Each call is synchronous — host returns immediately with bytes_read /
// bytes_written / status. Host can stash per-response state keyed by token.

typedef int (*excef_should_filter_response_cb_t)(int browser_id,
                                                   uint64_t filter_token,
                                                   const char* url,
                                                   int status,
                                                   const char* mime_type);
EXCEF_API void excef_set_should_filter_response_callback(excef_should_filter_response_cb_t cb);

// Filter callback. Sync. Per-chunk. Returns:
//   0 = NEED_MORE_DATA (call me again with more input)
//   1 = DONE (this response is finished — no more bytes will be sent)
//  -1 = ERROR (CEF will abort the response)
// data_in / data_out are caller-owned buffers; host writes into data_out.
typedef int (*excef_response_filter_cb_t)(int browser_id,
                                            uint64_t filter_token,
                                            const unsigned char* data_in,
                                            int size_in,
                                            unsigned char* data_out,
                                            int size_out,
                                            int* bytes_read,
                                            int* bytes_written);
EXCEF_API void excef_set_response_filter_callback(excef_response_filter_cb_t cb);

// Optional: fires once per filter end-of-life so the host can drop any
// per-token state (HTML parser, injection state machine, etc.).
typedef void (*excef_response_filter_finalize_cb_t)(int browser_id, uint64_t filter_token);
EXCEF_API void excef_set_response_filter_finalize_callback(excef_response_filter_finalize_cb_t cb);

// ---- Streaming resource handler (per-request URL intercept) ------------
//
// Lets the host serve a complete response for ANY URL — http://, file://,
// etc. Unlike the scheme handler factory (which is tied to a single scheme
// registered at init), this is a per-request decision made via the
// `should_handle_resource` callback. Most commonly used to intercept
// XHR/fetch requests, serve from a local mirror, or stub out endpoints
// in tests.
//
// Flow:
//   1. Host calls excef_set_should_handle_resource_callback() once.
//   2. Per request, the shim asks the host "claim this URL?" (sync).
//      Returning 1 allocates a token (same token pool as scheme handler)
//      and pauses CEF awaiting the response.
//   3. Host calls excef_resolve_resource_handler_request(token, ...) with
//      status / mime / headers / body, identical to the scheme path.

typedef int (*excef_should_handle_resource_cb_t)(int browser_id,
                                                   uint64_t token,
                                                   const char* url,
                                                   const char* method);
EXCEF_API void excef_set_should_handle_resource_callback(excef_should_handle_resource_cb_t cb);

// Provide the response for a token previously claimed via
// `should_handle_resource` (or `scheme_request`). headers is the raw header
// string (e.g. "Cache-Control: no-cache\nX-Custom: 1"), body is a copy-by-
// value byte array (shim copies before returning so the host can free).
EXCEF_API void excef_resolve_resource_handler_request(
    uint64_t token,
    int status_code,
    const char* status_text,
    const char* mime_type,
    const char* headers,
    const unsigned char* body,
    int body_len);

// ---- Command handler (Chrome runtime) -----------------------------------
//
// Intercept Chrome menu commands, page-action icons, toolbar buttons.
// Only meaningful when CEF is initialized with CEF_RUNTIME_STYLE_CHROME.
// All callbacks are synchronous host queries.

// command_id values are Chrome's IDC_* IDs (browser-side). disposition is
// cef_window_open_disposition_t — same enum values as JavaScript window.open.
// Return 1 to mark handled (CEF will NOT execute the default action).
typedef int (*excef_chrome_command_cb_t)(int browser_id, int command_id, int disposition);
EXCEF_API void excef_set_chrome_command_callback(excef_chrome_command_cb_t cb);

// App menu visibility/enabled queries. Return 1 = visible/enabled, 0 = hidden/disabled.
typedef int (*excef_app_menu_visibility_cb_t)(int browser_id, int command_id);
EXCEF_API void excef_set_app_menu_visible_callback(excef_app_menu_visibility_cb_t cb);
EXCEF_API void excef_set_app_menu_enabled_callback(excef_app_menu_visibility_cb_t cb);

// Page-action icon (e.g. bookmark star, find, manage passwords).
typedef int (*excef_page_action_visibility_cb_t)(int icon_type);
EXCEF_API void excef_set_page_action_visible_callback(excef_page_action_visibility_cb_t cb);

// Toolbar button (e.g. cast, download, avatar, side panel).
typedef int (*excef_toolbar_button_visibility_cb_t)(int button_type);
EXCEF_API void excef_set_toolbar_button_visible_callback(excef_toolbar_button_visibility_cb_t cb);

// Clipboard / editing primitives. Operate on the browser's focused frame.
// CEF in OSR mode does not auto-execute these from keyboard accelerators;
// the host must invoke them when Cmd/Ctrl + C / V / X / A / Z / Y is pressed.
EXCEF_API void excef_copy(int browser_id);
EXCEF_API void excef_paste(int browser_id);
EXCEF_API void excef_cut(int browser_id);
EXCEF_API void excef_select_all(int browser_id);
EXCEF_API void excef_undo(int browser_id);
EXCEF_API void excef_redo(int browser_id);

// ---- Drag and drop -------------------------------------------------------
//
// CEF in OSR mode does not interact with the platform's native drag-and-drop
// system. The host has to bridge: forward incoming OS drags to CEF as
// drag-target events, and forward CEF-initiated page drags out to the OS
// (or accept them internally) via the start-drag callback.
//
// DragOperationsMask values (subset of cef_drag_operations_mask_t):
#define EXCEF_DRAG_OP_NONE   0
#define EXCEF_DRAG_OP_COPY   1
#define EXCEF_DRAG_OP_LINK   2
#define EXCEF_DRAG_OP_GENERIC 4
#define EXCEF_DRAG_OP_PRIVATE 8
#define EXCEF_DRAG_OP_MOVE   16
#define EXCEF_DRAG_OP_DELETE 32
#define EXCEF_DRAG_OP_EVERY  0xFFFFFFFF

// Drag-target side: host calls these as the OS reports an external drag
// hovering / dropping over the WebView.
//
// `text`, `html`, `url`, and the `file_paths` array may be NULL or empty.
// `file_path_count` is the length of `file_paths`.
EXCEF_API void excef_drag_target_drag_enter(int browser_id,
                                            int x, int y,
                                            int modifiers,
                                            int allowed_ops,
                                            const char* text,
                                            const char* html,
                                            const char* url,
                                            const char** file_paths,
                                            int file_path_count);
EXCEF_API void excef_drag_target_drag_over(int browser_id,
                                            int x, int y,
                                            int modifiers,
                                            int allowed_ops);
EXCEF_API void excef_drag_target_drop(int browser_id,
                                      int x, int y,
                                      int modifiers);
EXCEF_API void excef_drag_target_drag_leave(int browser_id);

// Drag-image (ghost) callback: when the page starts a drag, CEF gives us a
// pre-rendered preview bitmap of the dragged element (CefDragData::GetImage).
// In OSR mode the host MUST draw this as an overlay following the cursor —
// CEF can't compose it onto the page itself. Buffer is BGRA8888, premultiplied
// alpha, valid only for the duration of the call (copy what you need).
// hotspot_{x,y} are the offset from the cursor to the image origin in DIPs.
// Buffer is NULL with width=height=0 if no preview image is available — host
// should fall back to a synthetic preview (e.g. the link text) or omit it.
typedef void (*excef_drag_image_cb_t)(int browser_id,
                                       const void* buffer,
                                       int width, int height,
                                       int hotspot_x, int hotspot_y);
EXCEF_API void excef_set_drag_image_callback(excef_drag_image_cb_t cb);

// Drag-source side: when the page initiates a drag, CEF calls this callback
// (set via excef_set_start_drag_callback) so the host can start an OS-level
// drag. Return 1 to indicate the host is handling it (host MUST eventually
// call excef_drag_source_ended_at + excef_drag_source_system_drag_ended);
// return 0 to fall back to internal-only DnD (the shim self-targets).
//
// The drag data is decomposed into individual fields. `link_url` / `text` /
// `html` / `file_names` are NULL/empty when not applicable. Do not retain
// the pointers past callback return — copy out anything you need.
typedef int (*excef_start_drag_cb_t)(int browser_id,
                                      int allowed_ops,
                                      int x, int y,
                                      const char* text,
                                      const char* html,
                                      const char* link_url,
                                      const char* link_title,
                                      const char** file_names,
                                      int file_name_count);
EXCEF_API void excef_set_start_drag_callback(excef_start_drag_cb_t cb);

// Host calls these once the OS-level drag finishes:
//   `op` is the operation that completed (EXCEF_DRAG_OP_*).
EXCEF_API void excef_drag_source_ended_at(int browser_id, int x, int y, int op);
EXCEF_API void excef_drag_source_system_drag_ended(int browser_id);

// ---- Input forwarding (OSR browsers) -------------------------------------
//
// Modifier flags — matches CEF's cef_event_flags_t subset.
#define EXCEF_MOD_NONE              0
#define EXCEF_MOD_CAPS_LOCK         (1 << 0)
#define EXCEF_MOD_SHIFT             (1 << 1)
#define EXCEF_MOD_CONTROL           (1 << 2)
#define EXCEF_MOD_ALT               (1 << 3)
#define EXCEF_MOD_LEFT_MOUSE        (1 << 4)
#define EXCEF_MOD_MIDDLE_MOUSE      (1 << 5)
#define EXCEF_MOD_RIGHT_MOUSE       (1 << 6)
#define EXCEF_MOD_COMMAND           (1 << 7)

// Mouse buttons — matches cef_mouse_button_type_t.
#define EXCEF_MBT_LEFT              0
#define EXCEF_MBT_MIDDLE            1
#define EXCEF_MBT_RIGHT             2

// Key event types — matches cef_key_event_type_t.
#define EXCEF_KEY_RAW_KEYDOWN       0
#define EXCEF_KEY_KEYDOWN           1
#define EXCEF_KEY_KEYUP             2
#define EXCEF_KEY_CHAR              3

EXCEF_API void excef_send_mouse_move(int browser_id, int x, int y,
                                     int modifiers, int mouse_leave);

EXCEF_API void excef_send_mouse_click(int browser_id, int x, int y,
                                      int button, int mouse_up,
                                      int click_count, int modifiers);

EXCEF_API void excef_send_mouse_wheel(int browser_id, int x, int y,
                                      int delta_x, int delta_y,
                                      int modifiers);

EXCEF_API void excef_send_key_event(int browser_id, int type,
                                    int windows_key_code, int native_key_code,
                                    int modifiers, int character,
                                    int unmodified_character,
                                    int is_system_key);

EXCEF_API void excef_set_browser_focus(int browser_id, int focus);

// ---- OSR low-level controls ----------------------------------------------
//
// All of these mirror CefBrowserHost methods. They mostly only make sense
// in windowless (OSR) mode — windowed Alloy / Chrome runtime browsers
// either ignore them or have their own paths.

// Force CEF to repaint a region. type: 0 = main view (PET_VIEW),
// 1 = popup (PET_POPUP).
EXCEF_API void excef_invalidate(int browser_id, int type);

// Tell CEF the OS captured the mouse away from the embedded page
// (alt-tab, lock screen, modal dialog). Without this, an in-flight
// drag can leave the page thinking the mouse is still pressed.
EXCEF_API void excef_send_capture_lost_event(int browser_id);

// Open the OS print dialog. PrintToPdfAsync is the other direction
// (silent, fixed settings) — use this for user-driven print flows.
EXCEF_API void excef_print(int browser_id);

// Programmatically start a download for the given URL.
EXCEF_API void excef_start_download(int browser_id, const char* url);

// NB: SetMouseCursorChangeDisabled / IsMouseCursorChangeDisabled were
// dropped from CefBrowserHost in CEF 147. For cursor-lock behaviour,
// intercept the CursorChanged event on CefBrowser at the managed side
// and override the host cursor — same effect, one layer up.

// Runtime-adjust the windowless frame rate. The browser-creation default
// is 30 fps; bump for media playback, drop to 1 for backgrounded tabs.
EXCEF_API void excef_set_windowless_frame_rate(int browser_id, int fps);
EXCEF_API int excef_get_windowless_frame_rate(int browser_id);

// Touch event. type: 0=released, 1=pressed, 2=moved, 3=cancelled.
// pointerType: 0=touch, 1=mouse, 2=pen, 3=eraser, 4=unknown.
EXCEF_API void excef_send_touch_event(int browser_id,
                                       int id,
                                       float x, float y,
                                       float radius_x, float radius_y,
                                       float rotation_angle,
                                       float pressure,
                                       int type,
                                       int modifiers,
                                       int pointer_type);

// Drive a single begin-frame to Chromium's compositor. Requires the
// browser to have been created with external_begin_frame_enabled=1
// (see excef_create_offscreen_browser_ex). Useful for deterministic
// frame timing in record / replay / agent loops.
EXCEF_API void excef_send_external_begin_frame(int browser_id);
// Start/stop a platform display-link clock that phase-aligns external begin
// frames to the hardware display and marshals them to CEF's UI thread. CEF
// 150's API reports a fixed 60 Hz interval and accepts no caller timestamp,
// so higher-refresh displays are sampled at 60 Hz. Currently supported on
// macOS.
EXCEF_API int excef_start_external_begin_frame_clock(int browser_id,
                                                      void* window_handle);
EXCEF_API void excef_stop_external_begin_frame_clock(int browser_id);

// ---- OSR render-handler events -----------------------------------------
//
// All four fire from the CEF UI thread. Hosts MUST hop to their UI
// thread before touching managed UI state.

// CEF asks the host for the touch-handle size (the little circles
// drawn at selection endpoints on touch screens). orientation: 0=left,
// 1=center, 2=right. Host writes *out_width / *out_height in DIPs.
// Default-zero result means "use Chromium's default".
typedef void (*excef_touch_handle_size_cb_t)(int browser_id,
                                                int orientation,
                                                int* out_width,
                                                int* out_height);
EXCEF_API void excef_set_touch_handle_size_callback(excef_touch_handle_size_cb_t cb);

// Touch handle state update. Fields are flattened from CefTouchHandleState
// — bit-test `flags` against cef_touch_handle_state_flags_t to know which
// fields are set on this update.
typedef void (*excef_touch_handle_state_cb_t)(int browser_id,
                                                 int touch_handle_id,
                                                 uint32_t flags,
                                                 int enabled,
                                                 int orientation,
                                                 int mirror_vertical,
                                                 int mirror_horizontal,
                                                 int origin_x,
                                                 int origin_y,
                                                 float alpha);
EXCEF_API void excef_set_touch_handle_state_callback(excef_touch_handle_state_cb_t cb);

// IME composition range changed — Chromium telling the host where the
// IME caret/composition rect is in view coordinates. Hosts wire this to
// the OS IME candidate window so it positions correctly.
// character_bounds is a flat array of 4*count ints (x,y,w,h per char) —
// caller-owned, valid for the duration of the callback.
typedef void (*excef_ime_composition_range_cb_t)(int browser_id,
                                                   int selection_start,
                                                   int selection_end,
                                                   int character_count,
                                                   const int* character_bounds);
EXCEF_API void excef_set_ime_composition_range_callback(excef_ime_composition_range_cb_t cb);

// On-screen keyboard request — page asked for a soft keyboard.
// input_mode: cef_text_input_mode_t (0=default, 1=none, 2=text, 3=tel,
// 4=url, 5=email, 6=numeric, 7=decimal, 8=search). 1 = hide.
typedef void (*excef_virtual_keyboard_cb_t)(int browser_id, int input_mode);
EXCEF_API void excef_set_virtual_keyboard_callback(excef_virtual_keyboard_cb_t cb);

// ---- Accelerated paint (GPU shared-texture path) -----------------------
//
// Only fires when the browser was created with shared_texture_enabled=1
// (excef_create_offscreen_browser_ex, flags & 0x2). Replaces the OnPaint
// CPU buffer path. The handle is platform-specific:
//   - macOS: IOSurfaceRef (cast the void* back to IOSurfaceRef)
//   - Windows: NT shared handle (HANDLE)
//   - Linux: gbm BO file descriptor (int via void*)
//
// The handle is owned by CEF and recycled — host code MUST copy / consume
// before this callback returns. format is cef_color_type_t (0=RGBA8888,
// 1=BGRA8888 — CEF header order; the managed CefColorType enum matches).
// timestamp_us is microseconds since capture start.
typedef void (*excef_accelerated_paint_cb_t)(int browser_id,
                                                int element_type,
                                                int coded_width,
                                                int coded_height,
                                                int format,
                                                uint64_t timestamp_us,
                                                const void* shared_handle);
EXCEF_API void excef_set_accelerated_paint_callback(excef_accelerated_paint_cb_t cb);

// macOS-only client-owned accelerated paint buffer. CEF recycles the source
// IOSurface as soon as OnAcceleratedPaint returns, so a compositor cannot
// safely retain that pointer. This helper performs a synchronous GPU-to-GPU
// Metal blit into the destination IOSurface. Passing a struct returned by an
// earlier successful call reuses its IOSurface, destination texture, and
// MTLSharedEvent when the dimensions and format still match. Initialize the
// struct to zero before its first use and call
// excef_release_macos_accelerated_frame when finished.
typedef struct excef_macos_accelerated_frame {
    void* io_surface;
    void* ready_event;
    void* destination_texture;
    uint64_t ready_value;
    int width;
    int height;
    int format;
} excef_macos_accelerated_frame;

EXCEF_API int excef_copy_macos_accelerated_frame(
    const void* source_io_surface,
    int width,
    int height,
    int format,
    excef_macos_accelerated_frame* out_frame);
// Returns 1 after the compositor has signaled ready_value + 1, or before the
// buffer has ever been submitted. A destination must not be overwritten while
// this function returns 0.
EXCEF_API int excef_macos_accelerated_frame_is_released(
    const excef_macos_accelerated_frame* frame);
EXCEF_API void excef_release_macos_accelerated_frame(
    excef_macos_accelerated_frame* frame);

// ---- Navigation / lifecycle ----------------------------------------------

EXCEF_API void excef_load_url(int browser_id, const char* url);
EXCEF_API void excef_go_back(int browser_id);
EXCEF_API void excef_go_forward(int browser_id);
EXCEF_API void excef_reload(int browser_id, int ignore_cache);
EXCEF_API void excef_stop_load(int browser_id);
EXCEF_API void excef_close_browser(int browser_id, int force_close);
EXCEF_API void excef_was_hidden(int browser_id, int hidden);

// ---- JavaScript ----------------------------------------------------------

// Execute JS in the main frame. Fire-and-forget — no return value.
EXCEF_API void excef_execute_javascript(int browser_id,
                                        const char* code,
                                        const char* script_url);

// ---- DevTools ------------------------------------------------------------

EXCEF_API void excef_show_dev_tools(int browser_id);
EXCEF_API void excef_close_dev_tools(int browser_id);

// ---- Browser events ------------------------------------------------------
//
// Process-wide callbacks: register once; CEF dispatches to them for any
// browser. The C# host filters by browser_id.

typedef void (*excef_address_change_cb_t)(int browser_id, const char* url);
typedef void (*excef_title_change_cb_t)(int browser_id, const char* title);
typedef void (*excef_loading_state_cb_t)(int browser_id,
                                          int is_loading,
                                          int can_go_back,
                                          int can_go_forward);

EXCEF_API void excef_set_address_change_callback(excef_address_change_cb_t cb);
EXCEF_API void excef_set_title_change_callback(excef_title_change_cb_t cb);
EXCEF_API void excef_set_loading_state_callback(excef_loading_state_cb_t cb);

// ---- Main-frame navigation gate ----------------------------------------
//
// CefRequestHandler::OnBeforeBrowse. Called synchronously on CEF's UI thread
// for top-level navigations only. Return 1 to cancel or 0 to allow. A canceled
// navigation subsequently reports ERR_ABORTED through the load-error event.
// `user_gesture` distinguishes clicks/key activation from scripted navigation;
// `is_redirect` identifies a server/client redirect continuation.
typedef int (*excef_before_browse_cb_t)(int browser_id,
                                         const char* url,
                                         int user_gesture,
                                         int is_redirect);
EXCEF_API void excef_set_before_browse_callback(excef_before_browse_cb_t cb);

// ---- JS evaluation with result ------------------------------------------
//
// Async eval. The host generates a request id, calls excef_eval_javascript,
// and waits for the registered callback to fire with that request id and
// the JSON-serialized result (or an error message).

typedef void (*excef_eval_result_cb_t)(int browser_id,
                                        int request_id,
                                        int success,
                                        const char* result_json);

EXCEF_API void excef_set_eval_result_callback(excef_eval_result_cb_t cb);

// Returns 1 if scheduled, 0 if browser_id is unknown.
EXCEF_API int excef_eval_javascript(int browser_id, int request_id,
                                    const char* code);

// ---- Cookies -------------------------------------------------------------
//
// Async iteration. Callback fires once per cookie with done=0, then once
// more with done=1 (cookie fields ignored on the done call).

typedef void (*excef_cookie_visit_cb_t)(int request_id,
                                         int done,
                                         const char* name,
                                         const char* value,
                                         const char* domain,
                                         const char* path,
                                         int secure,
                                         int httponly);

EXCEF_API void excef_set_cookie_visit_callback(excef_cookie_visit_cb_t cb);

// Iterate cookies for a URL (or all if url is NULL/empty).
// Returns the request_id used (>=1) on success, 0 on failure.
EXCEF_API int excef_get_cookies(const char* url, int request_id);

// Set a cookie. Returns 1 if scheduled.
EXCEF_API int excef_set_cookie(const char* url,
                               const char* name,
                               const char* value,
                               const char* domain,
                               const char* path,
                               int secure,
                               int httponly);

// Delete cookies matching url + name (either may be empty).
EXCEF_API void excef_delete_cookies(const char* url, const char* name);

// Same three operations on the cookie manager belonging to a specific
// CefRequestContext. context_handle = 0 → global manager (same as the
// non-suffixed variants). Lets the host partition cookies by profile.
EXCEF_API int excef_get_cookies_in_context(int context_handle, const char* url, int request_id);
EXCEF_API int excef_set_cookie_in_context(int context_handle, const char* url,
                                            const char* name, const char* value,
                                            const char* domain, const char* path,
                                            int secure, int httponly);
EXCEF_API void excef_delete_cookies_in_context(int context_handle, const char* url, const char* name);

// Completion callback for acknowledged request-context mutations. result is
// the deleted cookie count for cookie deletion and 1 for other completions.
typedef void (*excef_request_context_completion_cb_t)(int request_id, int result);
EXCEF_API void excef_set_request_context_completion_callback(
    excef_request_context_completion_cb_t cb);
EXCEF_API int excef_delete_cookies_in_context_async(
    int context_handle, const char* url, const char* name, int request_id);
EXCEF_API int excef_flush_cookie_store_async(int context_handle, int request_id);
// Applies a preference on the CEF UI thread after durable context initialization.
// Completion result is 1 only if the preference was actually accepted.
EXCEF_API int excef_set_preference_async(int context_handle, const char* name,
                                        const char* value_json, int request_id);

// ---- Browser-closed event ------------------------------------------------

typedef void (*excef_browser_closed_cb_t)(int browser_id);
EXCEF_API void excef_set_browser_closed_callback(excef_browser_closed_cb_t cb);

// ---- Cursor change event -------------------------------------------------
//
// `cursor_type` is a value from cef_cursor_type_t (CT_POINTER=0, CT_CROSS=1,
// CT_HAND=2, CT_IBEAM=3, CT_WAIT=4, CT_HELP=5, CT_EAST/NORTH/...RESIZE,
// CT_MOVE=29, CT_NOTALLOWED=38, CT_GRAB=41, CT_GRABBING=42, etc.). See
// CEF's include/internal/cef_types.h for the full list. Custom cursors
// (CSS `cursor: url(...)`) report CT_CUSTOM=45; the bitmap is not
// forwarded across the C ABI in this version.

typedef void (*excef_cursor_change_cb_t)(int browser_id, int cursor_type);
EXCEF_API void excef_set_cursor_change_callback(excef_cursor_change_cb_t cb);

// ---- Console-message event ----------------------------------------------
//
// CefDisplayHandler::OnConsoleMessage. Fires for every console.{log,info,
// warn,error,debug} call from the page (and for runtime warnings emitted
// by Chromium itself, e.g. CORS / deprecation). `level` is a value from
// cef_log_severity_t: 0=default, 1=verbose/debug, 2=info, 3=warning,
// 4=error, 5=fatal. `source` is the script URL (may be empty for inline
// scripts); `line` is 1-based.
typedef void (*excef_console_message_cb_t)(int browser_id,
                                             int level,
                                             const char* message,
                                             const char* source,
                                             int line);
EXCEF_API void excef_set_console_message_callback(excef_console_message_cb_t cb);

// ---- Per-frame Load events ----------------------------------------------
//
// CefLoadHandler. Fire once per frame; is_main_frame=1 lets hosts filter
// to top-level navigations. http_status_code is the actual HTTP status
// on LoadEnd (0 for non-HTTP loads / data: / file:). error_code on
// LoadError is from cef_errors.h (a negative integer; 0 = ERR_NONE).
typedef void (*excef_load_start_cb_t)(int browser_id, int is_main_frame, const char* url);
typedef void (*excef_load_end_cb_t)(int browser_id, int is_main_frame, const char* url, int http_status_code);
typedef void (*excef_load_error_cb_t)(int browser_id, int is_main_frame, int error_code, const char* error_text, const char* failed_url);
typedef void (*excef_loading_progress_cb_t)(int browser_id, double progress);

EXCEF_API void excef_set_load_start_callback(excef_load_start_cb_t cb);
EXCEF_API void excef_set_load_end_callback(excef_load_end_cb_t cb);
EXCEF_API void excef_set_load_error_callback(excef_load_error_cb_t cb);
EXCEF_API void excef_set_loading_progress_callback(excef_loading_progress_cb_t cb);

// ---- Display events (status / tooltip / favicon / fullscreen) -----------
//
// All CefDisplayHandler. Strings are non-null but may be empty.
// `first_url` on favicon is the highest-priority icon URL from the
// page's <link rel="icon" …> tags (we pass the first; hosts that want
// the full list can call back into the page via JS). `fullscreen` is 1
// when the page enters HTML5 fullscreen, 0 when it leaves.
typedef void (*excef_status_message_cb_t)(int browser_id, const char* value);
typedef void (*excef_tooltip_cb_t)(int browser_id, const char* text);
typedef void (*excef_favicon_cb_t)(int browser_id, const char* first_url);
typedef void (*excef_fullscreen_cb_t)(int browser_id, int fullscreen);

EXCEF_API void excef_set_status_message_callback(excef_status_message_cb_t cb);
EXCEF_API void excef_set_tooltip_callback(excef_tooltip_cb_t cb);
EXCEF_API void excef_set_favicon_callback(excef_favicon_cb_t cb);
EXCEF_API void excef_set_fullscreen_callback(excef_fullscreen_cb_t cb);

// Exit page-driven fullscreen. `will_cause_resize=1` lets Chromium know
// the host will resize the view in response, suppressing redundant layout
// flicker.
EXCEF_API void excef_exit_fullscreen(int browser_id, int will_cause_resize);

// ---- Browser-initialised event ------------------------------------------
//
// CefLifeSpanHandler::OnAfterCreated. Fires once per browser, on the CEF
// UI thread, after the underlying CefBrowser is fully constructed and
// available to host operations (frame access, clipboard, navigation, …).
// The browser id returned by excef_create_offscreen_browser is valid
// immediately, but ops that need the CefBrowser ref are no-ops until
// this fires.
typedef void (*excef_browser_initialized_cb_t)(int browser_id);
EXCEF_API void excef_set_browser_initialized_callback(excef_browser_initialized_cb_t cb);

// ---- Render-handler observation: scroll & auto-resize -------------------
//
// CefRenderHandler::OnScrollOffsetChanged fires whenever the page's
// scroll position changes (smooth-scroll updates included — many times
// per second during a flick). x/y are in CSS pixels.
typedef void (*excef_scroll_offset_cb_t)(int browser_id, double x, double y);
EXCEF_API void excef_set_scroll_offset_callback(excef_scroll_offset_cb_t cb);

// CefRenderHandler::OnAutoResize fires when the page content's natural
// size changes; only delivered if auto-resize is enabled on the host
// side. Width/height are in CSS pixels.
typedef void (*excef_auto_resize_cb_t)(int browser_id, int width, int height);
EXCEF_API void excef_set_auto_resize_callback(excef_auto_resize_cb_t cb);

// Toggle CefBrowserHost auto-resize. When enabled, Chromium re-measures
// the page after every layout and emits an OnAutoResize callback if the
// natural content size differs from the current view rect. Pass
// `enabled=0` to disable; min/max are in DIPs / CSS pixels (ignored when
// disabling).
EXCEF_API void excef_set_auto_resize_enabled(int browser_id, int enabled,
                                              int min_w, int min_h,
                                              int max_w, int max_h);

// Zoom availability: query CefBrowserHost::CanZoom. `command` matches
// cef_zoom_command_t — 0=ZOOM_OUT, 1=ZOOM_RESET, 2=ZOOM_IN. Returns 0/1.
// Useful for graying out zoom UI controls at min/max zoom.
EXCEF_API int excef_can_zoom(int browser_id, int command);

// ---- Deferred-response handlers ----------------------------------------
//
// Pattern: a CEF handler callback in the renderer's main process hands us
// a callback object (CefJSDialogCallback, CefFileDialogCallback, etc.). We
// stash the callback in a per-handler-type map keyed by a uint64_t token,
// fire the C# event with the token + payload, and return true to CEF
// (meaning "we'll respond async"). C# decides on its UI thread and calls
// the matching `excef_resolve_*` to invoke Continue / Cancel on the
// stored callback (the resolve hops to the CEF UI thread internally).
//
// Resolve calls on unknown tokens are no-ops — safe if C# resolves twice
// or after browser close. Pending callbacks for a browser are cancelled
// automatically when that browser closes.

// ---- JS dialog handler -------------------------------------------------
//
// CefJSDialogHandler::OnJSDialog + OnBeforeUnloadDialog. dialog_type:
//   0 = alert       (user_input ignored on resolve)
//   1 = confirm     (success only)
//   2 = prompt      (user_input is the typed text on success)
//   3 = onbeforeunload (success=1 means "leave page"; user_input ignored)
typedef void (*excef_js_dialog_cb_t)(
    int browser_id,
    uint64_t token,
    int dialog_type,
    const char* message_text,
    const char* default_prompt_text);

EXCEF_API void excef_set_js_dialog_callback(excef_js_dialog_cb_t cb);

// Continue the pending JS dialog. `success` = 1 if the user accepted
// (clicked OK / Leave); `user_input` is the prompt text on accept
// (NULL/empty otherwise).
EXCEF_API void excef_resolve_js_dialog(uint64_t token,
                                        int success,
                                        const char* user_input);

// ---- File dialog handler -----------------------------------------------
//
// CefDialogHandler::OnFileDialog. Fires when the page triggers a file
// chooser (<input type=file>, showOpenFilePicker, downloads with
// Save-As, etc.). `mode`:
//   0 = open single file
//   1 = open multiple files
//   2 = open folder
//   3 = save file
// `accept_filters` is newline-separated (each line is a MIME type
// like "image/*" or a glob like "*.txt"). Empty if no filter.
typedef void (*excef_file_dialog_cb_t)(
    int browser_id,
    uint64_t token,
    int mode,
    const char* title,
    const char* default_file_path,
    const char* accept_filters);

EXCEF_API void excef_set_file_dialog_callback(excef_file_dialog_cb_t cb);

// Resolve. `paths` is newline-separated absolute paths (UTF-8). Pass
// NULL or empty string to cancel (the page sees an empty selection).
EXCEF_API void excef_resolve_file_dialog(uint64_t token, const char* paths);

// ---- Context-menu handler ----------------------------------------------
//
// CefContextMenuHandler::RunContextMenu. Fires on right-click / long-press.
// The page-side `params` includes the click coordinates and selection /
// link / image info; we expose x,y here and serialize each top-level item as
// "<id>\t<kind>\t<enabled>\t<checked>\t<depth>\t<label>". Kinds
// 0/1/2/3/4 are command/check/radio/separator/submenu, and depth preserves
// the native menu hierarchy.
//
// In OSR mode CEF cannot render the menu itself, so the host MUST render
// its own and call excef_resolve_context_menu with the chosen command id
// (or -1 to cancel). Without a host subscriber the menu is suppressed.
typedef void (*excef_context_menu_cb_t)(
    int browser_id,
    uint64_t token,
    int x, int y,
    const char* items_joined);

EXCEF_API void excef_set_context_menu_callback(excef_context_menu_cb_t cb);

// Resolve with the chosen command id, or -1 to dismiss without action.
EXCEF_API void excef_resolve_context_menu(uint64_t token, int command_id);

// ---- Download handler --------------------------------------------------
//
// CefDownloadHandler has two distinct hooks:
//
// 1. `OnBeforeDownload` — fires once at the start. Host must pick a
//    save path (or cancel). Standard deferred-response pattern:
//    download-starting callback fires, host resolves with path.
//
// 2. `OnDownloadUpdated` — fires repeatedly while the download is in
//    flight. Each invocation carries a *fresh* token; the host can
//    optionally call excef_download_action(token, ...) synchronously
//    inside the event handler to Cancel / Pause / Resume. Tokens are
//    invalidated after the handler returns.
typedef void (*excef_download_starting_cb_t)(
    int browser_id,
    uint64_t token,
    int download_id,
    const char* url,
    const char* suggested_name,
    const char* mime_type,
    long long total_bytes);

EXCEF_API void excef_set_download_starting_callback(excef_download_starting_cb_t cb);

// Resolve. `path` = NULL/"" cancels the download. `show_dialog` = 1
// makes the OS show a save-as dialog (in addition to using `path`).
EXCEF_API void excef_resolve_download_starting(uint64_t token,
                                                 const char* path,
                                                 int show_dialog);

// state: 0 = in-progress, 1 = complete, 2 = canceled.
// percent_complete may be -1 if total_bytes is unknown.
typedef void (*excef_download_progress_cb_t)(
    int browser_id,
    uint64_t token,
    int download_id,
    int percent_complete,
    long long received_bytes,
    long long total_bytes,
    long long current_speed,
    int state,
    const char* full_path);

EXCEF_API void excef_set_download_progress_callback(excef_download_progress_cb_t cb);

// action: 0 = cancel, 1 = pause, 2 = resume. No-op on unknown tokens
// (the token is invalidated as soon as the event handler returns).
EXCEF_API void excef_download_action(uint64_t token, int action);

// ---- Auth credentials handler ------------------------------------------
//
// CefRequestHandler::GetAuthCredentials. Fires when the server (or proxy)
// returns 401/407 with an HTTP-Basic, Digest, or NTLM challenge. Host
// supplies username + password via excef_resolve_auth, or cancels by
// passing NULL for both.
typedef void (*excef_auth_request_cb_t)(
    int browser_id,
    uint64_t token,
    int is_proxy,
    const char* host,
    int port,
    const char* realm,
    const char* scheme,
    const char* origin_url);

EXCEF_API void excef_set_auth_request_callback_v2(excef_auth_request_cb_t cb);

// Resolve. NULL/empty username = cancel (the request fails with 401/407).
EXCEF_API void excef_resolve_auth(uint64_t token,
                                    const char* username,
                                    const char* password);

// ---- Find handler -------------------------------------------------------
//
// CefFindHandler::OnFindResult. Fires once or more per Find() call to
// report match count, ordinal of the active match, and whether this is
// the final update for the search session. `identifier` is a CEF-internal
// id tying results to a search.
typedef void (*excef_find_result_cb_t)(
    int browser_id,
    int identifier,
    int count,
    int active_match_ordinal,
    int final_update);

EXCEF_API void excef_set_find_result_callback(excef_find_result_cb_t cb);

// Begin (or update) an in-page search. `find_next` = 1 to jump to next
// match while keeping the previous search alive; 0 starts a new search.
// `forward` = direction; `match_case` = case sensitivity.
EXCEF_API void excef_find(int browser_id,
                           const char* search_text,
                           int forward,
                           int match_case,
                           int find_next);

// Stop the in-page search. `clear_selection` = 1 to also remove the
// orange highlight on the last match.
EXCEF_API void excef_stop_finding(int browser_id, int clear_selection);

// ---- Init settings ------------------------------------------------------
//
// A useful subset of CefSettings the host can configure before calling
// excef_initialize_offscreen / excef_initialize_external_pump / etc.
// All fields are optional:
//   - char* strings: NULL/empty = use CEF default
//   - int booleans: 0 = use CEF default
//   - log_severity: 0 = LOGSEVERITY_DEFAULT (which is Info in release builds)
//
// We intentionally do not expose fields the shim controls internally:
// browser_subprocess_path, framework_dir_path, multi_threaded_message_loop,
// external_message_pump, windowless_rendering_enabled,
// command_line_args_disabled, no_sandbox.
typedef struct excef_init_settings {
    const char* cache_path;             // disk cache + cookies. NULL = in-memory.
    const char* root_cache_path;        // parent for browser caches; defaults to system app-data.
    const char* user_agent;             // full UA override (replaces Chromium's)
    const char* user_agent_product;     // product-token suffix ("MyApp/1.0"); leaves the rest
    const char* locale;                 // "en-US"; controls UI locale + accept-language fallback
    const char* accept_language_list;   // "en-US,en;q=0.9"; overrides locale-derived default
    const char* log_file;               // NULL/empty = stderr
    const char* javascript_flags;       // V8 flags, e.g. "--max-old-space-size=512"
    int log_severity;                   // cef_log_severity_t
    int persist_session_cookies;        // 0/1; requires cache_path
    int remote_debugging_port;          // 0 = disabled, else 1024..65535
    int enable_javascript_bridge;       // 0 = disabled (default); 1 = inject window.exclr8cef
} excef_init_settings;

// Stash settings to apply on the next init call. Pass NULL to clear back
// to defaults. The struct is copied; pointers don't need to outlive the call.
EXCEF_API void excef_set_init_settings(const excef_init_settings* settings);

// ---- Certificate error handler -----------------------------------------
//
// CefRequestHandler::OnCertificateError. Fires when a TLS connection
// can't be verified (self-signed, expired, hostname mismatch, …). Without
// a subscriber, CEF reports the error as a normal load failure (the page
// shows Chromium's interstitial). With a subscriber, the host receives
// the URL + subject/issuer common names and decides to proceed or block.
//
// `cert_error` is from cef_errorcode_t (negative int, e.g.
// ERR_CERT_AUTHORITY_INVALID=-202, ERR_CERT_DATE_INVALID=-201).
//
// Deferred-response pattern — host calls excef_resolve_cert_error(token,
// proceed): proceed=1 continues the load (treat cert as trusted for this
// request only); proceed=0 cancels (page sees the load failure).
typedef void (*excef_cert_error_cb_t)(int browser_id,
                                        uint64_t token,
                                        int cert_error,
                                        const char* request_url,
                                        const char* subject_common_name,
                                        const char* issuer_common_name);

EXCEF_API void excef_set_cert_error_callback(excef_cert_error_cb_t cb);
EXCEF_API void excef_resolve_cert_error(uint64_t token, int proceed);

// ---- LifeSpan: OnBeforePopup -------------------------------------------
//
// CefLifeSpanHandler::OnBeforePopup. Fires when the page tries to open a
// new browser window (window.open(), target="_blank" navigations, Ctrl-click
// link, etc.). Note: this is the *new-browser* popup path, distinct from
// the CefRenderHandler popup paint pipeline used for `<select>` dropdowns
// and autocomplete — those continue to work regardless of this hook.
//
// Without a subscriber, CEF proceeds with the default behavior (creates a
// popup browser, which in OSR mode is mostly unusable). With a subscriber,
// the popup is ALWAYS cancelled at the CEF layer; the host receives the
// URL via this callback and decides what to do (open externally via
// Process.Start, create a new WebView, suppress, …).
//
// `disposition` mirrors cef_window_open_disposition_t: 0=unknown, 1=current
// tab, 5=new popup, 6=new window, etc. `user_gesture` is 1 if a user
// interaction (click) initiated the open, 0 if scripted.
typedef void (*excef_before_popup_cb_t)(int browser_id,
                                          const char* target_url,
                                          const char* target_frame_name,
                                          int disposition,
                                          int user_gesture);
EXCEF_API void excef_set_before_popup_callback(excef_before_popup_cb_t cb);

// Reserve and adopt the actual OSR popup before CEF creates it. Return 1 only
// after the managed host owns child_browser_id and its policy/lifetime. The
// native window stays windowless and retains CEF's opener/WindowProxy.
typedef int (*excef_host_popup_cb_t)(int browser_id, int child_browser_id,
                                    const char* target_url,
                                    const char* target_frame_name,
                                    int disposition, int user_gesture);
EXCEF_API void excef_set_host_popup_callback(excef_host_popup_cb_t cb);

// ---- Permission handler ------------------------------------------------
//
// Two distinct hooks. Both follow the deferred-response pattern.
//
// `_permission_prompt_cb_t` (CefPermissionHandler::OnShowPermissionPrompt)
//   Generic permission asks — notifications, geolocation, clipboard, MIDI,
//   pointer-lock, … `requested_permissions` is a bitmask from
//   cef_permission_request_types_t (CEF_PERMISSION_TYPE_*). Resolve with
//   excef_resolve_permission_prompt(token, result) where result is from
//   cef_permission_request_result_t:
//     0 = accept, 1 = deny, 2 = dismiss, 3 = ignore.
//
// `_media_access_cb_t` (CefPermissionHandler::OnRequestMediaAccessPermission)
//   getUserMedia camera/microphone/screen requests. `requested_permissions`
//   is a bitmask from cef_media_access_permission_types_t (CEF_MEDIA_*).
//   Resolve with excef_resolve_media_access(token, granted_permissions),
//   where granted is a subset of requested. Pass 0 to deny.

typedef void (*excef_permission_prompt_cb_t)(
    int browser_id,
    uint64_t token,
    uint64_t prompt_id,
    const char* requesting_origin,
    int requested_permissions);

EXCEF_API void excef_set_permission_prompt_callback(excef_permission_prompt_cb_t cb);

EXCEF_API void excef_resolve_permission_prompt(uint64_t token, int result);

typedef void (*excef_media_access_cb_t)(
    int browser_id,
    uint64_t token,
    const char* requesting_origin,
    int requested_permissions);

EXCEF_API void excef_set_media_access_callback(excef_media_access_cb_t cb);

EXCEF_API void excef_resolve_media_access(uint64_t token, int granted_permissions);

// ---- Render-process termination ----------------------------------------
//
// CefRequestHandler::OnRenderProcessTerminated. The renderer subprocess
// hosting this browser's page died. `status` is from
// cef_termination_status_t:
//   0 = abnormal termination (generic non-zero exit)
//   1 = process was killed (SIGTERM / TerminateProcess)
//   2 = process crashed (SIGSEGV / access violation)
//   3 = out of memory
//   4 = launch failed (subprocess couldn't start)
//   5 = integrity failure (CEF integrity check failed)
// The browser's OSR paint buffer freezes after termination; hosts
// typically respond by calling Reload() on the affected browser.
typedef void (*excef_render_process_gone_cb_t)(int browser_id,
                                                 int status,
                                                 int error_code,
                                                 const char* error_string);
EXCEF_API void excef_set_render_process_gone_callback(excef_render_process_gone_cb_t cb);

// ---- Custom scheme + resource handler ----------------------------------
//
// Register a URL scheme (e.g. "app") that routes through a host-side
// callback. The host returns full response bytes for each request — no
// streaming in v1, just a single buffer per response.
//
// Workflow:
//   1. Host calls excef_register_custom_scheme(...) BEFORE init for every
//      scheme it wants to handle. The scheme is registered with Chromium
//      and (if `register_factory` is non-zero) hooked to our internal
//      resource-handler factory.
//   2. Host calls excef_set_scheme_request_callback() to receive requests.
//   3. When a page navigates to `app://...` or fetches `app://...`, the
//      callback fires with a fresh token + the URL + the HTTP method.
//   4. Host calls excef_resolve_scheme_request(token, ...) with the
//      response (status code, mime type, headers, body bytes).
//
// All buffers are copied; callers do not need to keep them alive past the
// resolve call. To return a 404, pass status_code=404 with empty body.

// is_standard:        1 = treat URLs as standard (have host, path, etc.);
//                     0 = opaque (everything after "scheme:" is the path)
// is_local:           1 = local (file-like) — blocks XHR from non-local
// is_display_isolated:1 = can only be displayed in <iframe> from same origin
// is_secure:          1 = treated as "secure context" (enables crypto APIs)
// is_cors_enabled:    1 = allow CORS requests
// is_csp_bypassing:   1 = exempt from Content-Security-Policy
//
// Returns 0 on success, non-zero on failure.
EXCEF_API int excef_register_custom_scheme(const char* scheme_name,
                                            int is_standard,
                                            int is_local,
                                            int is_display_isolated,
                                            int is_secure,
                                            int is_cors_enabled,
                                            int is_csp_bypassing);

typedef void (*excef_scheme_request_cb_t)(
    int browser_id,
    uint64_t token,
    const char* url,
    const char* method);

EXCEF_API void excef_set_scheme_request_callback(excef_scheme_request_cb_t cb);

// Resolve a pending scheme request with the response body + metadata.
// `mime_type` is optional (NULL/empty → CEF infers from the URL).
// `body` may be NULL when `body_length == 0`. Multiple resolves on the
// same token are no-ops (idempotent).
EXCEF_API void excef_resolve_scheme_request(
    uint64_t token,
    int status_code,
    const char* status_text,
    const char* mime_type,
    const unsigned char* body,
    int body_length);

// ---- Resource-request handler (lite) -----------------------------------
//
// Fires once per outgoing network request — main-frame navigations,
// sub-resources (CSS / JS / images / favicons), XHR/fetch, web workers.
// Host can inspect the URL / method / current headers and either let
// the request proceed (optionally replacing the header set) or cancel.
//
// `resource_type` mirrors cef_resource_type_t (0=main_frame, 1=sub_frame,
// 2=stylesheet, 3=script, 4=image, 5=font, 6=sub_resource, 7=object,
// 8=media, 9=worker, 12=favicon, 13=xhr, 15=service_worker, ...).
//
// `current_headers` is the request's current header set serialized as
// `Name: Value\n` lines (no trailing newline).
typedef void (*excef_resource_request_cb_t)(
    int browser_id,
    uint64_t token,
    const char* url,
    const char* method,
    int resource_type,
    const char* current_headers);

EXCEF_API void excef_set_resource_request_callback(excef_resource_request_cb_t cb);

// Resolve. `action` = 0 → continue, 1 → cancel. If `action=0` and
// `new_headers` is non-NULL, the request's entire header set is REPLACED
// with the supplied list (same `Name: Value\n` format). Pass NULL or
// empty to keep existing headers untouched.
EXCEF_API void excef_resolve_resource_request(
    uint64_t token,
    int action,
    const char* new_headers);

// ---- OSR popup support --------------------------------------------------
//
// Page popups (<select> dropdowns, autocomplete, etc.) paint into a
// *separate* buffer and at a *different* position from the main view. CEF
// signals popup lifecycle through three callbacks:
//
//   excef_popup_show_cb_t   — toggle visibility (1 = show, 0 = hide)
//   excef_popup_size_cb_t   — popup rect in DIP / CSS pixels, relative to
//                             the browser's main view origin
//   excef_popup_paint_cb_t  — popup bitmap (BGRA8888, physical pixels;
//                             same shape as the regular paint callback)
//
// Hosts that don't wire these callbacks will see popups silently dropped
// (the previous behaviour). With them wired and rendered as an overlay
// at the popup rect, <select> dropdowns and HTML popups Just Work.
typedef void (*excef_popup_show_cb_t)(int browser_id, int show);
typedef void (*excef_popup_size_cb_t)(int browser_id, int x, int y, int width, int height);
typedef void (*excef_popup_paint_cb_t)(int browser_id, const void* buffer, int width, int height);

EXCEF_API void excef_set_popup_show_callback(excef_popup_show_cb_t cb);
EXCEF_API void excef_set_popup_size_callback(excef_popup_size_cb_t cb);
EXCEF_API void excef_set_popup_paint_callback(excef_popup_paint_cb_t cb);

// ---- JavaScript bridge (lite) -------------------------------------------
//
// When excef_init_settings.enable_javascript_bridge is 1, installs
// `window.exclr8cef.invoke(method, argsJson)` in each browser's main-frame V8
// context. The function is absent by default and never injected into iframes.
// Calls from JS fire a
// CefProcessMessage to the browser process; we surface that to the host via
// this callback. `args_json` is whatever JS passed as the second argument
// (typically a JSON.stringify(...) of an args object). Host parses it
// however it likes.
//
// `token` lets the host send a reply back to the renderer's Promise via
// excef_resolve_js_invoke. If the host never resolves, the Promise stays
// pending — clean it up on browser close.
typedef void (*excef_js_invoke_cb_t)(int browser_id,
                                       uint64_t token,
                                       const char* method,
                                       const char* args_json);
EXCEF_API void excef_set_js_invoke_callback(excef_js_invoke_cb_t cb);

// Resolve a pending window.exclr8cef.invoke() promise.
// `success` = 1 → renderer's Promise resolves with JSON.parse(result_json)
// `success` = 0 → renderer's Promise rejects with result_json (string)
// `result_json` may be NULL/empty (treated as "null"). Idempotent — extra
// resolve calls on the same token are no-ops.
EXCEF_API void excef_resolve_js_invoke(uint64_t token,
                                        int success,
                                        const char* result_json);

// ---- Navigation: LoadRequest + history --------------------------------
//
// Beyond LoadUrl (GET, no headers), CEF lets you load via CefRequest:
// arbitrary HTTP method, post body, custom headers, referrer policy.
// The main-frame is the target.

EXCEF_API int excef_load_request(int browser_id,
                                  const char* method,
                                  const char* url,
                                  const unsigned char* post_body,
                                  int post_length,
                                  const char* headers_string);

// Navigation history — visit entries via CefBrowserHost::GetNavigationEntries.
// `current_only` = 1 yields just the current entry; 0 yields all.
// Fires the visitor callback once per entry, terminated with done=1.
typedef void (*excef_nav_entry_cb_t)(int request_id,
                                       int done,
                                       int is_current,
                                       const char* url,
                                       const char* display_url,
                                       const char* original_url,
                                       const char* title,
                                       int transition_type,
                                       int http_status_code,
                                       long long completion_time_ms,
                                       int is_valid);
EXCEF_API void excef_set_nav_entry_callback(excef_nav_entry_cb_t cb);
EXCEF_API int excef_get_navigation_entries(int browser_id,
                                            int request_id,
                                            int current_only);

// ---- Frame operations -------------------------------------------------
//
// Operate on the browser's main frame. CefFrame::GetSource gives the
// HTML, GetText gives the rendered plain text, LoadString loads an HTML
// string directly without a network round-trip (useful for splash pages
// or rendering generated content).

typedef void (*excef_string_visitor_cb_t)(int request_id, const char* value);
EXCEF_API void excef_set_string_visitor_callback(excef_string_visitor_cb_t cb);

// Returns 0 on failure, 1 on success. The result is delivered async via
// the string-visitor callback with the same request_id.
EXCEF_API int excef_get_frame_source(int browser_id, int request_id);
EXCEF_API int excef_get_frame_text(int browser_id, int request_id);

// Load `html` into the main frame. Small documents travel as a
// data:text/html;base64 URL (the location bar reflects that URL and
// relative-link resolution is against it). Documents whose encoded URL
// would exceed Chromium's 2 MB URL cap (≈1.5 MB of raw HTML) cannot use
// that transport — Chromium silently drops over-long navigations at the
// IPC boundary, no load event ever fires — so they are served instead
// through an internal in-memory resource handler under
// https://loadstring.exclr8cef.internal/<token> (unlimited size, normal
// load events, synthetic secure origin). One document is retained per
// browser (so reloads work) until the next LoadString or browser close.
// Returns 1 on success, 0 if the browser is unknown/closed.
EXCEF_API int excef_load_string(int browser_id,
                                 const char* html);

// ---- Init: extra Chromium switches ------------------------------------
//
// Append a Chromium command-line switch (with optional value) that
// `OnBeforeCommandLineProcessing` will apply to the browser process. Chromium
// forwards supported switches to subprocesses. Feature-list switches are
// merged with values that CEF configured before the callback. Use for things like
// "enable-features=WebGPU,WebAssemblyDynamicTiering" or
// "disable-blink-features=AutomationControlled". MUST be called before
// any Initialize* function.
EXCEF_API void excef_add_command_line_switch(const char* name, const char* value);

// ---- DevTools protocol (CDP) messaging --------------------------------
//
// CefBrowserHost::SendDevToolsMessage / ExecuteDevToolsMethod /
// AddDevToolsMessageObserver. Lets the host drive the browser via the
// Chrome DevTools Protocol — the universal escape hatch for things not
// exposed in CEF's high-level handlers (Network.setUserAgentOverride,
// Emulation.setDeviceMetricsOverride, Page.captureScreenshot,
// Network.setBlockedURLs, Runtime.evaluate with isolated worlds, etc.).

// Send a raw CDP JSON message. `message_json` must include "id" if you
// want a reply. Returns 1 if sent, 0 if browser is unknown.
EXCEF_API int excef_send_devtools_message(int browser_id,
                                           const char* message_json,
                                           int message_length);

// Execute a CDP method with a structured params object. Returns the
// CDP message_id (positive int) so the host can correlate the eventual
// response in the observer callback. Returns 0 on failure.
EXCEF_API int excef_execute_devtools_method(int browser_id,
                                             int message_id,
                                             const char* method,
                                             const char* params_json);

// Observer fires for every CDP message — replies to host-sent requests
// AND server-pushed events (Network.requestWillBeSent, Page.loadEventFired,
// etc.). `is_event` = 1 for unsolicited events, 0 for replies. `message_id`
// is the int id from the request (0 for events).
typedef void (*excef_devtools_message_cb_t)(int browser_id,
                                              int is_event,
                                              int message_id,
                                              const char* message_json);
EXCEF_API void excef_set_devtools_message_callback(excef_devtools_message_cb_t cb);

// ---- Focus handler -----------------------------------------------------
//
// CefFocusHandler. Lets the host integrate the page's focus chain with
// its surrounding UI toolkit:
//
//   _take_focus_cb_t  — page wants to move focus OUT (Tab past last form
//                       field, Shift+Tab before first). `next=1` for
//                       forward, 0 for backward. Host should move focus
//                       to the next/previous control around the WebView.
//   _set_focus_cb_t   — CEF wants to TAKE focus (page click, system
//                       request). `source`: 0=navigation, 1=system.
//                       Return 1 to deny, 0 to allow.
//   _got_focus_cb_t   — CEF received focus successfully.

typedef void (*excef_take_focus_cb_t)(int browser_id, int next);
typedef int  (*excef_set_focus_cb_t)(int browser_id, int source);
typedef void (*excef_got_focus_cb_t)(int browser_id);

EXCEF_API void excef_set_take_focus_callback(excef_take_focus_cb_t cb);
EXCEF_API void excef_set_set_focus_callback(excef_set_focus_cb_t cb);
EXCEF_API void excef_set_got_focus_callback(excef_got_focus_cb_t cb);

// ---- Keyboard handler -------------------------------------------------
//
// CefKeyboardHandler. Lets the host intercept key events before / after
// the page sees them. Useful for global accelerators (Ctrl+F, Ctrl+R,
// F11 fullscreen, etc.) and for gating dev-tools shortcuts.
//
// `event_type`: 0=raw-keydown, 1=keydown, 2=keyup, 3=char (cef_key_event_type_t)
// `modifiers`: bitmask of EXCEF_MOD_*
// `windows_key_code`: Windows VK code
// `native_key_code`: platform-specific scan code
// Returns 1 to mark the event handled (suppress page dispatch), 0 to allow.
typedef int (*excef_pre_key_cb_t)(int browser_id,
                                    int event_type,
                                    int modifiers,
                                    int windows_key_code,
                                    int native_key_code,
                                    int is_system_key);
// OnKeyEvent: fires AFTER the page sees the event (and didn't handle it).
// Same args as pre-key. Return 1 to indicate host handled it.
typedef int (*excef_key_event_cb_t)(int browser_id,
                                     int event_type,
                                     int modifiers,
                                     int windows_key_code,
                                     int native_key_code,
                                     int is_system_key);

EXCEF_API void excef_set_pre_key_callback(excef_pre_key_cb_t cb);
EXCEF_API void excef_set_key_event_callback(excef_key_event_cb_t cb);

// ---- Accessibility (lite) ----------------------------------------------
//
// CEF can emit Chromium's accessibility tree as updates, intended to be
// reflected into the host UI framework's automation peer hierarchy
// (NSAccessibility / UIA / AT-SPI). Building a full AutomationPeer
// integration is a sizeable project; v1 here just surfaces the raw
// updates as JSON for hosts that want to consume them.
//
// Disabled by default — call excef_set_accessibility_enabled(id, 1)
// before tree/location callbacks fire. Disabling pauses delivery but
// the tree is still maintained in the renderer.
typedef void (*excef_accessibility_tree_cb_t)(int browser_id, const char* tree_json);
typedef void (*excef_accessibility_location_cb_t)(int browser_id, const char* locations_json);

EXCEF_API void excef_set_accessibility_tree_callback(excef_accessibility_tree_cb_t cb);
EXCEF_API void excef_set_accessibility_location_callback(excef_accessibility_location_cb_t cb);

// Enable / disable the a11y stream. State is per-browser. 0 = disabled,
// 1 = enabled. (CEF supports a third "auto" state — wired to OS
// accessibility detection — but that's not exposed here in v1.)
EXCEF_API void excef_set_accessibility_enabled(int browser_id, int enabled);

// ---- Request context (per-browser isolation) ---------------------------
//
// CefRequestContext lets browsers share or partition cookies, cache,
// localStorage, and other per-origin state. A browser created in context
// A has a completely separate cookie jar / cache / storage from one in
// context B — the pattern behind "incognito tabs", "multi-profile",
// "container" extensions, etc.
//
// Usage:
//   1. Host calls excef_create_request_context(cache_path) to get a
//      context handle (>= 1). Pass NULL/empty cache_path for an
//      in-memory (incognito-style) context.
//   2. Host passes the handle to excef_create_offscreen_browser_in_context
//      (or 0 for the global context, equivalent to the existing
//      excef_create_offscreen_browser).
//   3. Host calls excef_release_request_context when the context is no
//      longer needed — outstanding browsers keep it alive; once the last
//      browser closes, CEF tears the context down.
//
// Returns a positive handle on success, 0 on failure. The handle is
// stable for the process lifetime; reuse it across many browsers if you
// want them to share state.
EXCEF_API int excef_create_request_context(const char* cache_path);

// Drop our refcount on the context. The CEF instance is still kept alive
// by any in-flight browsers using it.
EXCEF_API void excef_release_request_context(int context_handle);

// Preferences (Chromium-internal settings). Keyed by dotted name:
// `proxy.mode`, `webrtc.ip_handling_policy`, `intl.accept_languages`,
// `safebrowsing.enabled`, `autoplay.policy`, `spellcheck.languages`, etc.
// `value_json` is JSON-encoded (string, number, bool, object, array).
// Use context_handle=0 for the global request context.
// Returns 1 on success.
EXCEF_API int excef_set_preference(int context_handle,
                                     const char* name,
                                     const char* value_json);

// Returns the pref as JSON, or NULL if unset. Caller must free via
// excef_free_string. Use context_handle=0 for the global context.
EXCEF_API const char* excef_get_preference(int context_handle, const char* name);

// Frees a string returned by excef_get_preference / similar APIs.
EXCEF_API void excef_free_string(const char* s);

// Network controls per-context. Useful for sign-out / identity switching.
// Returns 1 on success.
EXCEF_API int excef_clear_http_auth_credentials(int context_handle);
EXCEF_API int excef_close_all_connections(int context_handle);
EXCEF_API int excef_clear_http_auth_credentials_async(
    int context_handle, int request_id);
EXCEF_API int excef_close_all_connections_async(
    int context_handle, int request_id);
// Note: ClearCertificateExceptions was removed from CefRequestContext in
// recent CEF; equivalent path is via DevTools Security.setIgnoreCertificateErrors.

// Variant of excef_create_offscreen_browser that creates the browser in
// a specific request context. Pass context_handle=0 to use the global
// default context (equivalent to excef_create_offscreen_browser).
EXCEF_API int excef_create_offscreen_browser_in_context(
    int width, int height, float device_scale_factor,
    const char* url,
    excef_paint_callback_t paint,
    int context_handle);

// Extended variant: combines _in_context with a flags bitmask for the
// rarer CefWindowInfo toggles.
//   bit 0 (0x1) = external_begin_frame_enabled — required for
//                  excef_send_external_begin_frame; the standard
//                  windowless_frame_rate timer stops driving frames.
//   bit 1 (0x2) = shared_texture_enabled — CEF will deliver paints via
//                  excef_accelerated_paint_cb_t (GPU shared-texture
//                  handle) instead of the BGRA buffer in OnPaint.
//                  The host must do platform-specific GPU interop to
//                  consume the handle (IOSurface on macOS, NT handle on
//                  Windows, dma-buf on Linux).
EXCEF_API int excef_create_offscreen_browser_ex(
    int width, int height, float device_scale_factor,
    const char* url,
    excef_paint_callback_t paint,
    int context_handle,
    int flags);


// ---- IME -----------------------------------------------------------------
//
// Forwards composition events to CEF. Avalonia IME integration uses these
// from a TextInputMethodClient implementation; minimum useful set:

EXCEF_API void excef_ime_set_composition(int browser_id,
                                          const char* text,
                                          int replacement_range_from,
                                          int replacement_range_length,
                                          int selection_range_from,
                                          int selection_range_length);

EXCEF_API void excef_ime_commit_text(int browser_id,
                                     const char* text,
                                     int replacement_range_from,
                                     int replacement_range_length,
                                     int relative_cursor_pos);

EXCEF_API void excef_ime_finish_composing(int browser_id, int keep_selection);
EXCEF_API void excef_ime_cancel(int browser_id);

// ---- PrintToPDF ----------------------------------------------------------
//
// Render the browser's current page as a PDF at `path`. Async — `callback`
// fires when complete with success=1 on success, 0 on failure.
//
// Advanced print settings (header/footer templates, margins, paper size)
// live in the optional extension header `exclr8cef_print.h`. Apps that
// don't need PDF customization can ignore that header and bind only to
// `excef_print_to_pdf` here.

typedef void (*excef_pdf_done_callback_t)(int browser_id, int success);

// Returns 1 if the request was scheduled, 0 if browser_id is unknown.
EXCEF_API int excef_print_to_pdf(int browser_id,
                                 const char* path,
                                 excef_pdf_done_callback_t callback);

#ifdef __cplusplus
}
#endif

#endif  // EXCLR8CEF_H_
