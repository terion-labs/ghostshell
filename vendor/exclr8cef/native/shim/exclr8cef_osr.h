// CefClient implementation for off-screen-rendering browsers.
// Handles paint, lifecycle, address/title/loading-state, IPC eval results.

#ifndef EXCLR8CEF_OSR_H_
#define EXCLR8CEF_OSR_H_

#include <atomic>
#include <cstdint>
#include <mutex>
#include <map>

#include "include/cef_client.h"
#include "include/cef_context_menu_handler.h"
#include "include/cef_dialog_handler.h"
#include "include/cef_display_handler.h"
#include "include/cef_download_handler.h"
#include "include/cef_find_handler.h"
#include "include/cef_jsdialog_handler.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_accessibility_handler.h"
#include "include/cef_audio_handler.h"
#include "include/cef_command_handler.h"
#include "include/cef_focus_handler.h"
#include "include/cef_frame_handler.h"
#include "include/cef_keyboard_handler.h"
#include "include/cef_load_handler.h"
#include "include/cef_permission_handler.h"
#include "include/cef_render_handler.h"
#include "include/cef_request_handler.h"
#include "include/cef_resource_request_handler.h"
#include "include/cef_response_filter.h"

#include "exclr8cef.h"

namespace exclr8cef {

class Exclr8CefOsrHandler : public CefClient,
                            public CefRenderHandler,
                            public CefLifeSpanHandler,
                            public CefDisplayHandler,
                            public CefLoadHandler,
                            public CefJSDialogHandler,
                            public CefDialogHandler,
                            public CefContextMenuHandler,
                            public CefDownloadHandler,
                            public CefRequestHandler,
                            public CefFindHandler,
                            public CefResourceRequestHandler,
                            public CefAccessibilityHandler,
                            public CefPermissionHandler,
                            public CefFocusHandler,
                            public CefKeyboardHandler,
                            public CefFrameHandler,
                            public CefAudioHandler,
                            public CefCommandHandler {
public:
    Exclr8CefOsrHandler(int id, int width, int height, float device_scale_factor,
                        excef_paint_callback_t paint_cb);

    // CefClient
    // GetRenderHandler returns nullptr when no paint callback is registered
    // — that's how we reuse this class for the embedded (non-OSR) browser
    // path. CEF in windowed mode won't request OSR rendering if we say no
    // render handler, but all the other handlers (load / display / drag /
    // permission / …) still fire so the event surface is the same.
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return paint_cb_ ? this : nullptr; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefJSDialogHandler> GetJSDialogHandler() override { return this; }
    CefRefPtr<CefDialogHandler> GetDialogHandler() override { return this; }
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
    CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }
    CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
    CefRefPtr<CefFindHandler> GetFindHandler() override { return this; }
    CefRefPtr<CefAccessibilityHandler> GetAccessibilityHandler() override { return this; }
    CefRefPtr<CefPermissionHandler> GetPermissionHandler() override { return this; }
    CefRefPtr<CefFocusHandler> GetFocusHandler() override { return this; }
    CefRefPtr<CefKeyboardHandler> GetKeyboardHandler() override { return this; }
    CefRefPtr<CefFrameHandler> GetFrameHandler() override { return this; }
    // Only opt in to audio capture when the host has installed a packet
    // callback — otherwise CEF spends cycles streaming PCM nobody reads.
    CefRefPtr<CefAudioHandler> GetAudioHandler() override;
    CefRefPtr<CefCommandHandler> GetCommandHandler() override { return this; }

    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
                                  CefRefPtr<CefFrame> frame,
                                  CefProcessId source_process,
                                  CefRefPtr<CefProcessMessage> message) override;

    // CefRenderHandler
    void GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect) override;
    bool GetScreenInfo(CefRefPtr<CefBrowser> browser, CefScreenInfo& info) override;
    void OnPaint(CefRefPtr<CefBrowser> browser,
                 PaintElementType type,
                 const RectList& dirtyRects,
                 const void* buffer,
                 int width, int height) override;

    void OnPopupShow(CefRefPtr<CefBrowser> browser, bool show) override;
    void OnPopupSize(CefRefPtr<CefBrowser> browser, const CefRect& rect) override;

    // OSR-specific render-handler events the host can subscribe to.
    void OnAcceleratedPaint(CefRefPtr<CefBrowser> browser,
                              PaintElementType type,
                              const RectList& dirtyRects,
                              const CefAcceleratedPaintInfo& info) override;
    void GetTouchHandleSize(CefRefPtr<CefBrowser> browser,
                              cef_horizontal_alignment_t orientation,
                              CefSize& size) override;
    void OnTouchHandleStateChanged(CefRefPtr<CefBrowser> browser,
                                     const CefTouchHandleState& state) override;
    void OnImeCompositionRangeChanged(CefRefPtr<CefBrowser> browser,
                                        const CefRange& selected_range,
                                        const RectList& character_bounds) override;
    void OnVirtualKeyboardRequested(CefRefPtr<CefBrowser> browser,
                                       TextInputMode input_mode) override;

    // CefAccessibilityHandler
    void OnAccessibilityTreeChange(CefRefPtr<CefValue> value) override;
    void OnAccessibilityLocationChange(CefRefPtr<CefValue> value) override;
    void OnScrollOffsetChanged(CefRefPtr<CefBrowser> browser,
                                double x, double y) override;

    bool OnAutoResize(CefRefPtr<CefBrowser> browser,
                       const CefSize& new_size) override;

    // CefJSDialogHandler
    bool OnJSDialog(CefRefPtr<CefBrowser> browser,
                    const CefString& origin_url,
                    JSDialogType dialog_type,
                    const CefString& message_text,
                    const CefString& default_prompt_text,
                    CefRefPtr<CefJSDialogCallback> callback,
                    bool& suppress_message) override;

    bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                               const CefString& message_text,
                               bool is_reload,
                               CefRefPtr<CefJSDialogCallback> callback) override;

    // CefDialogHandler
    bool OnFileDialog(CefRefPtr<CefBrowser> browser,
                       FileDialogMode mode,
                       const CefString& title,
                       const CefString& default_file_path,
                       const std::vector<CefString>& accept_filters,
                       const std::vector<CefString>& accept_extensions,
                       const std::vector<CefString>& accept_descriptions,
                       CefRefPtr<CefFileDialogCallback> callback) override;

    // CefContextMenuHandler
    void OnBeforeContextMenu(CefRefPtr<CefBrowser> browser,
                              CefRefPtr<CefFrame> frame,
                              CefRefPtr<CefContextMenuParams> params,
                              CefRefPtr<CefMenuModel> model) override;

    bool RunContextMenu(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame,
                         CefRefPtr<CefContextMenuParams> params,
                         CefRefPtr<CefMenuModel> model,
                         CefRefPtr<CefRunContextMenuCallback> callback) override;

    // CefDownloadHandler
    bool OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefDownloadItem> download_item,
                           const CefString& suggested_name,
                           CefRefPtr<CefBeforeDownloadCallback> callback) override;

    void OnDownloadUpdated(CefRefPtr<CefBrowser> browser,
                            CefRefPtr<CefDownloadItem> download_item,
                            CefRefPtr<CefDownloadItemCallback> callback) override;

    // CefRequestHandler
    bool OnOpenURLFromTab(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefFrame> frame,
                           const CefString& target_url,
                           WindowOpenDisposition target_disposition,
                           bool user_gesture) override;

    bool OnBeforeBrowse(CefRefPtr<CefBrowser> browser,
                        CefRefPtr<CefFrame> frame,
                        CefRefPtr<CefRequest> request,
                        bool user_gesture,
                        bool is_redirect) override;

    bool GetAuthCredentials(CefRefPtr<CefBrowser> browser,
                             const CefString& origin_url,
                             bool isProxy,
                             const CefString& host,
                             int port,
                             const CefString& realm,
                             const CefString& scheme,
                             CefRefPtr<CefAuthCallback> callback) override;

    void OnRenderProcessTerminated(CefRefPtr<CefBrowser> browser,
                                    TerminationStatus status,
                                    int error_code,
                                    const CefString& error_string) override;

    bool OnCertificateError(CefRefPtr<CefBrowser> browser,
                              cef_errorcode_t cert_error,
                              const CefString& request_url,
                              CefRefPtr<CefSSLInfo> ssl_info,
                              CefRefPtr<CefCallback> callback) override;

    // Return ourselves as the resource handler for every request — keeps
    // the v1 implementation flat (one CefResourceRequestHandler instance
    // shared, not one per request).
    CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefRefPtr<CefRequest> request,
        bool is_navigation,
        bool is_download,
        const CefString& request_initiator,
        bool& disable_default_handling) override;

    // CefResourceRequestHandler — only OnBeforeResourceLoad in v1.
    cef_return_value_t OnBeforeResourceLoad(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefRefPtr<CefRequest> request,
        CefRefPtr<CefCallback> callback) override;

    // Body rewrite via response filter (CefResponseFilter). Returns a
    // filter instance for responses the host wants to rewrite; nullptr
    // for everything else (lets CEF stream the response unchanged).
    CefRefPtr<CefResponseFilter> GetResourceResponseFilter(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefRefPtr<CefRequest> request,
        CefRefPtr<CefResponse> response) override;

    // Custom resource handler. Host claims a URL via the
    // should_handle_resource callback; if claimed, we return a deferred
    // handler that the host fills in via excef_resolve_resource_handler_request.
    CefRefPtr<CefResourceHandler> GetResourceHandler(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefRefPtr<CefRequest> request) override;

    // CefFindHandler
    void OnFindResult(CefRefPtr<CefBrowser> browser,
                       int identifier,
                       int count,
                       const CefRect& selectionRect,
                       int activeMatchOrdinal,
                       bool finalUpdate) override;

    // CefPermissionHandler
    bool OnRequestMediaAccessPermission(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        const CefString& requesting_origin,
        uint32_t requested_permissions,
        CefRefPtr<CefMediaAccessCallback> callback) override;

    bool OnShowPermissionPrompt(
        CefRefPtr<CefBrowser> browser,
        uint64_t prompt_id,
        const CefString& requesting_origin,
        uint32_t requested_permissions,
        CefRefPtr<CefPermissionPromptCallback> callback) override;

    // CefFocusHandler
    void OnTakeFocus(CefRefPtr<CefBrowser> browser, bool next) override;
    bool OnSetFocus(CefRefPtr<CefBrowser> browser, FocusSource source) override;
    void OnGotFocus(CefRefPtr<CefBrowser> browser) override;

    // CefKeyboardHandler
    bool OnPreKeyEvent(CefRefPtr<CefBrowser> browser,
                        const CefKeyEvent& event,
                        CefEventHandle os_event,
                        bool* is_keyboard_shortcut) override;
    bool OnKeyEvent(CefRefPtr<CefBrowser> browser,
                     const CefKeyEvent& event,
                     CefEventHandle os_event) override;

    // CefAudioHandler
    bool GetAudioParameters(CefRefPtr<CefBrowser> browser,
                              CefAudioParameters& params) override;
    void OnAudioStreamStarted(CefRefPtr<CefBrowser> browser,
                                const CefAudioParameters& params,
                                int channels) override;
    void OnAudioStreamPacket(CefRefPtr<CefBrowser> browser,
                               const float** data,
                               int frames,
                               int64_t pts) override;
    void OnAudioStreamStopped(CefRefPtr<CefBrowser> browser) override;
    void OnAudioStreamError(CefRefPtr<CefBrowser> browser,
                              const CefString& message) override;

    // CefCommandHandler
    bool OnChromeCommand(CefRefPtr<CefBrowser> browser, int command_id,
                          cef_window_open_disposition_t disposition) override;
    bool IsChromeAppMenuItemVisible(CefRefPtr<CefBrowser> browser, int command_id) override;
    bool IsChromeAppMenuItemEnabled(CefRefPtr<CefBrowser> browser, int command_id) override;
    bool IsChromePageActionIconVisible(cef_chrome_page_action_icon_type_t icon_type) override;
    bool IsChromeToolbarButtonVisible(cef_chrome_toolbar_button_type_t button_type) override;

    // CefFrameHandler
    void OnFrameCreated(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame) override;
    void OnFrameAttached(CefRefPtr<CefBrowser> browser,
                          CefRefPtr<CefFrame> frame,
                          bool reattached) override;
    void OnFrameDetached(CefRefPtr<CefBrowser> browser,
                          CefRefPtr<CefFrame> frame) override;
    void OnMainFrameChanged(CefRefPtr<CefBrowser> browser,
                              CefRefPtr<CefFrame> old_frame,
                              CefRefPtr<CefFrame> new_frame) override;

    bool StartDragging(CefRefPtr<CefBrowser> browser,
                       CefRefPtr<CefDragData> drag_data,
                       DragOperationsMask allowed_ops,
                       int x, int y) override;
    void UpdateDragCursor(CefRefPtr<CefBrowser> browser,
                          DragOperation operation) override;

    // CefLifeSpanHandler
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;
    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override;
    void OnBeforePopupAborted(CefRefPtr<CefBrowser> browser, int popup_id) override;
    bool OnBeforePopup(CefRefPtr<CefBrowser> browser,
                        CefRefPtr<CefFrame> frame,
                        int popup_id,
                        const CefString& target_url,
                        const CefString& target_frame_name,
                        cef_window_open_disposition_t target_disposition,
                        bool user_gesture,
                        const CefPopupFeatures& popupFeatures,
                        CefWindowInfo& windowInfo,
                        CefRefPtr<CefClient>& client,
                        CefBrowserSettings& settings,
                        CefRefPtr<CefDictionaryValue>& extra_info,
                        bool* no_javascript_access) override;

    // CefDisplayHandler
    void OnAddressChange(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame,
                         const CefString& url) override;
    void OnTitleChange(CefRefPtr<CefBrowser> browser,
                       const CefString& title) override;

    // CefLoadHandler
    void OnLoadingStateChange(CefRefPtr<CefBrowser> browser,
                              bool isLoading,
                              bool canGoBack,
                              bool canGoForward) override;

    void OnLoadStart(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     TransitionType transition_type) override;

    void OnLoadEnd(CefRefPtr<CefBrowser> browser,
                   CefRefPtr<CefFrame> frame,
                   int httpStatusCode) override;

    void OnLoadError(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     ErrorCode errorCode,
                     const CefString& errorText,
                     const CefString& failedUrl) override;

    void OnLoadingProgressChange(CefRefPtr<CefBrowser> browser,
                                  double progress) override;

    // CefDisplayHandler
    bool OnCursorChange(CefRefPtr<CefBrowser> browser,
                        CefCursorHandle cursor,
                        cef_cursor_type_t type,
                        const CefCursorInfo& custom_cursor_info) override;

    bool OnConsoleMessage(CefRefPtr<CefBrowser> browser,
                          cef_log_severity_t level,
                          const CefString& message,
                          const CefString& source,
                          int line) override;

    void OnStatusMessage(CefRefPtr<CefBrowser> browser,
                         const CefString& value) override;

    bool OnTooltip(CefRefPtr<CefBrowser> browser,
                   CefString& text) override;

    void OnFaviconURLChange(CefRefPtr<CefBrowser> browser,
                            const std::vector<CefString>& icon_urls) override;

    void OnFullscreenModeChange(CefRefPtr<CefBrowser> browser,
                                 bool fullscreen) override;

    int id() const { return id_; }
    int width() const {
        std::lock_guard<std::mutex> lock(size_mu_);
        return width_;
    }
    int height() const {
        std::lock_guard<std::mutex> lock(size_mu_);
        return height_;
    }
    float device_scale_factor() const { return device_scale_factor_; }
    // browser_ is written on the CEF UI thread (OnAfterCreated /
    // OnBeforeClose) but read from the host's caller thread and the CEF IO
    // thread (SchemeFactory reverse lookup), so the cross-thread accessor
    // must copy the ref under a lock. UI-thread-only code inside the
    // handler keeps using browser_ directly.
    CefRefPtr<CefBrowser> browser() const {
        std::lock_guard<std::mutex> lock(browser_mu_);
        return browser_;
    }

    void SetSize(int width, int height);
    // Runs only on CEF's UI thread. Public so the ref-counted CefTask used
    // to marshal/coalesce host resize requests can invoke it safely.
    void ApplyPendingSizeOnUi();
    void ApplySettledSizeOnUi(uint64_t scheduled_generation);
    void SetDeviceScaleFactor(float scale);
    void ForgetPendingPopup(int popup_id);
    void AbortPendingPopup();

    // Drag-and-drop bookkeeping. The shim handles internal-page DnD entirely
    // by intercepting mouse events while in_drag_ is true and converting
    // them to DragTarget* calls on the browser host.
    bool is_in_drag() const { return in_drag_; }
    DragOperationsMask drag_allowed_ops() const { return drag_allowed_ops_; }
    DragOperation drag_current_op() const { return drag_current_op_; }
    void clear_drag() {
        in_drag_ = false;
        drag_data_ = nullptr;
        drag_allowed_ops_ = DRAG_OPERATION_NONE;
        drag_current_op_ = DRAG_OPERATION_NONE;
    }

private:
    bool ForwardNewTabRequest(const CefString& target_url,
                               const CefString& target_frame_name,
                               cef_window_open_disposition_t target_disposition,
                               bool user_gesture);

    void QueuePendingSizeOnUi();
    void QueueSettledSizeOnUi();
    bool PostSettledSizeTask(uint64_t generation);

    int id_;
    // Requested DIP / CSS-pixel size. Avalonia writes these on its UI
    // thread while CEF reads them on TID_UI in GetViewRect/GetScreenInfo.
    mutable std::mutex size_mu_;
    int width_;
    int height_;
    std::atomic<bool> resize_task_pending_{false};
    std::atomic<uint64_t> resize_generation_{0};
    std::atomic<bool> settled_resize_task_pending_{false};
    float device_scale_factor_;
    excef_paint_callback_t paint_cb_;
    mutable std::mutex browser_mu_;
    std::map<int, CefRefPtr<Exclr8CefOsrHandler>> pending_popups_;
    int popup_opener_id_ = 0;
    int popup_request_id_ = 0;
    bool popup_aborted_ = false;
    CefRefPtr<CefBrowser> browser_;
    // Channel count from OnAudioStreamStarted; OnAudioStreamPacket's data
    // array is exactly this long (no null terminator). Both fire on the
    // CEF audio thread, in order, so no synchronization is needed.
    int audio_channels_ = 0;

    bool in_drag_ = false;
    CefRefPtr<CefDragData> drag_data_;
    DragOperationsMask drag_allowed_ops_ = DRAG_OPERATION_NONE;
    DragOperation drag_current_op_ = DRAG_OPERATION_NONE;

    IMPLEMENT_REFCOUNTING(Exclr8CefOsrHandler);
};

// Look up the CefBrowser for an OSR browser id created via
// excef_create_offscreen_browser. Returns nullptr if unknown.
// Exposed so optional extensions (e.g. exclr8cef_print.cc) can act on a
// browser without reaching into the OSR handler map directly.
CefRefPtr<CefBrowser> GetOsrBrowser(int browser_id);

// Embedded-browser helpers — let exclr8cef_mac.mm reuse the OSR handler
// (with paint_cb=null) for windowed browsers so the same event surface
// fires through the same trampolines.
int AllocateBrowserId();
void RegisterOsrHandler(int browser_id, CefRefPtr<Exclr8CefOsrHandler> handler);
void UnregisterOsrHandler(int browser_id);
// Returns a ref-counted handle (not a raw pointer): the registry can be
// erased on the CEF UI thread while the caller is still using the handler.
CefRefPtr<Exclr8CefOsrHandler> LookupOsrHandler(int browser_id);

// Forward decl so platform shims (mac.mm / win.cc) can resolve a request-
// context handle from C# to the underlying CefRequestContext before
// passing it to CefBrowserHost::CreateBrowser. Defined in exclr8cef_osr.cc.
CefRefPtr<CefRequestContext> ResolveContext(int handle);

}  // namespace exclr8cef

#endif  // EXCLR8CEF_OSR_H_
