using System.Runtime.InteropServices;

namespace Exclr8Cef.Native;

internal static unsafe partial class Excef
{
    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_get_versions(excef_versions* @out);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_execute_process(int argc, [NativeTypeName("char **")] sbyte** argv);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_initialize(int argc, [NativeTypeName("char **")] sbyte** argv, [NativeTypeName("const char *")] sbyte* subprocess_path);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_create_browser([NativeTypeName("const char *")] sbyte* url);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_run_message_loop();

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_quit_message_loop();

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_shutdown();

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_initialize_external_pump(int argc, [NativeTypeName("char **")] sbyte** argv, [NativeTypeName("const char *")] sbyte* subprocess_path, [NativeTypeName("excef_schedule_pump_work_t")] delegate* unmanaged[Cdecl]<long, void> schedule_callback);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_do_message_loop_work();

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void* excef_create_browser_view(int width, int height, [NativeTypeName("const char *")] sbyte* url, int* out_browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void* excef_create_embedded_host(int width, int height);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void* excef_create_embedded_host_in_parent(void* parent, int width, int height);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_destroy_embedded_host(void* host_view);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_attach_embedded_browser(void* host_view, int width, int height, [NativeTypeName("const char *")] sbyte* url);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void* excef_create_browser_view_in_context(int width, int height, [NativeTypeName("const char *")] sbyte* url, int* out_browser_id, int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_attach_embedded_browser_in_context(void* host_view, int width, int height, [NativeTypeName("const char *")] sbyte* url, int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_attach_embedded_browser_in_context_v2(void* host_view, int width, int height, [NativeTypeName("const char *")] sbyte* url, int context_handle, [NativeTypeName("uint32_t")] uint background_color);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resize_browser_view(void* host_view, int width, int height);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_embedded_host_hidden(void* host_view, int hidden);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_initialize_offscreen(int argc, [NativeTypeName("char **")] sbyte** argv, [NativeTypeName("const char *")] sbyte* subprocess_path, [NativeTypeName("excef_schedule_pump_work_t")] delegate* unmanaged[Cdecl]<long, void> schedule_callback);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_create_offscreen_browser(int width, int height, float device_scale_factor, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("excef_paint_callback_t")] delegate* unmanaged[Cdecl]<int, void*, int, int, void> paint);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resize_offscreen_browser(int browser_id, int width, int height);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_device_scale_factor(int browser_id, float scale);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_zoom_level(int browser_id, double level);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern double excef_get_zoom_level(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_notify_move_or_resize_started(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_notify_screen_info_changed(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_replace_misspelling(int browser_id, [NativeTypeName("const char *")] sbyte* word);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_add_word_to_dictionary(int browser_id, [NativeTypeName("const char *")] sbyte* word);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_frame_lifecycle_callback([NativeTypeName("excef_frame_lifecycle_cb_t")] delegate* unmanaged[Cdecl]<int, int, sbyte*, sbyte*, sbyte*, sbyte*, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_main_frame_changed_callback([NativeTypeName("excef_main_frame_changed_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_enable_audio_capture(int browser_id, int enable);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_audio_stream_started_callback([NativeTypeName("excef_audio_stream_started_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_audio_stream_packet_callback([NativeTypeName("excef_audio_stream_packet_cb_t")] delegate* unmanaged[Cdecl]<int, float*, int, int, long, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_audio_stream_stopped_callback([NativeTypeName("excef_audio_stream_stopped_cb_t")] delegate* unmanaged[Cdecl]<int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_audio_stream_error_callback([NativeTypeName("excef_audio_stream_error_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_audio_muted(int browser_id, int muted);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_is_audio_muted(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_should_filter_response_callback([NativeTypeName("excef_should_filter_response_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, int, sbyte*, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_response_filter_callback([NativeTypeName("excef_response_filter_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, byte*, int, byte*, int, int*, int*, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_response_filter_finalize_callback([NativeTypeName("excef_response_filter_finalize_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_should_handle_resource_callback([NativeTypeName("excef_should_handle_resource_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, sbyte*, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_resource_handler_request([NativeTypeName("uint64_t")] ulong token, int status_code, [NativeTypeName("const char *")] sbyte* status_text, [NativeTypeName("const char *")] sbyte* mime_type, [NativeTypeName("const char *")] sbyte* headers, [NativeTypeName("const unsigned char *")] byte* body, int body_len);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_chrome_command_callback([NativeTypeName("excef_chrome_command_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_app_menu_visible_callback([NativeTypeName("excef_app_menu_visibility_cb_t")] delegate* unmanaged[Cdecl]<int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_app_menu_enabled_callback([NativeTypeName("excef_app_menu_visibility_cb_t")] delegate* unmanaged[Cdecl]<int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_page_action_visible_callback([NativeTypeName("excef_page_action_visibility_cb_t")] delegate* unmanaged[Cdecl]<int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_toolbar_button_visible_callback([NativeTypeName("excef_toolbar_button_visibility_cb_t")] delegate* unmanaged[Cdecl]<int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_copy(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_paste(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_cut(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_select_all(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_undo(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_redo(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_target_drag_enter(int browser_id, int x, int y, int modifiers, int allowed_ops, [NativeTypeName("const char *")] sbyte* text, [NativeTypeName("const char *")] sbyte* html, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char **")] sbyte** file_paths, int file_path_count);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_target_drag_over(int browser_id, int x, int y, int modifiers, int allowed_ops);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_target_drop(int browser_id, int x, int y, int modifiers);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_target_drag_leave(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_drag_image_callback([NativeTypeName("excef_drag_image_cb_t")] delegate* unmanaged[Cdecl]<int, void*, int, int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_start_drag_callback([NativeTypeName("excef_start_drag_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, sbyte*, sbyte*, sbyte*, sbyte*, sbyte**, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_source_ended_at(int browser_id, int x, int y, int op);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_drag_source_system_drag_ended(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_mouse_move(int browser_id, int x, int y, int modifiers, int mouse_leave);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_mouse_click(int browser_id, int x, int y, int button, int mouse_up, int click_count, int modifiers);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_mouse_wheel(int browser_id, int x, int y, int delta_x, int delta_y, int modifiers);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_key_event(int browser_id, int type, int windows_key_code, int native_key_code, int modifiers, int character, int unmodified_character, int is_system_key);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_browser_focus(int browser_id, int focus);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_invalidate(int browser_id, int type);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_capture_lost_event(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_print(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_start_download(int browser_id, [NativeTypeName("const char *")] sbyte* url);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_windowless_frame_rate(int browser_id, int fps);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_windowless_frame_rate(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_touch_event(int browser_id, int id, float x, float y, float radius_x, float radius_y, float rotation_angle, float pressure, int type, int modifiers, int pointer_type);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_send_external_begin_frame(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_start_external_begin_frame_clock(int browser_id, void* window_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_stop_external_begin_frame_clock(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_touch_handle_size_callback([NativeTypeName("excef_touch_handle_size_cb_t")] delegate* unmanaged[Cdecl]<int, int, int*, int*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_touch_handle_state_callback([NativeTypeName("excef_touch_handle_state_cb_t")] delegate* unmanaged[Cdecl]<int, int, uint, int, int, int, int, int, int, float, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_ime_composition_range_callback([NativeTypeName("excef_ime_composition_range_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_virtual_keyboard_callback([NativeTypeName("excef_virtual_keyboard_cb_t")] delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_accelerated_paint_callback([NativeTypeName("excef_accelerated_paint_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, ulong, void*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_copy_macos_accelerated_frame([NativeTypeName("const void *")] void* source_io_surface, int width, int height, int format, excef_macos_accelerated_frame* out_frame);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_macos_accelerated_frame_is_released([NativeTypeName("const excef_macos_accelerated_frame *")] excef_macos_accelerated_frame* frame);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_release_macos_accelerated_frame(excef_macos_accelerated_frame* frame);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_load_url(int browser_id, [NativeTypeName("const char *")] sbyte* url);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_go_back(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_go_forward(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_reload(int browser_id, int ignore_cache);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_stop_load(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_close_browser(int browser_id, int force_close);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_was_hidden(int browser_id, int hidden);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_execute_javascript(int browser_id, [NativeTypeName("const char *")] sbyte* code, [NativeTypeName("const char *")] sbyte* script_url);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_show_dev_tools(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_close_dev_tools(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_address_change_callback([NativeTypeName("excef_address_change_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_title_change_callback([NativeTypeName("excef_title_change_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_loading_state_callback([NativeTypeName("excef_loading_state_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_before_browse_callback([NativeTypeName("excef_before_browse_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_eval_result_callback([NativeTypeName("excef_eval_result_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_eval_javascript(int browser_id, int request_id, [NativeTypeName("const char *")] sbyte* code);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_cookie_visit_callback([NativeTypeName("excef_cookie_visit_cb_t")] delegate* unmanaged[Cdecl]<int, int, sbyte*, sbyte*, sbyte*, sbyte*, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_cookies([NativeTypeName("const char *")] sbyte* url, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_set_cookie([NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char *")] sbyte* name, [NativeTypeName("const char *")] sbyte* value, [NativeTypeName("const char *")] sbyte* domain, [NativeTypeName("const char *")] sbyte* path, int secure, int httponly);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_delete_cookies([NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char *")] sbyte* name);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_cookies_in_context(int context_handle, [NativeTypeName("const char *")] sbyte* url, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_set_cookie_in_context(int context_handle, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char *")] sbyte* name, [NativeTypeName("const char *")] sbyte* value, [NativeTypeName("const char *")] sbyte* domain, [NativeTypeName("const char *")] sbyte* path, int secure, int httponly);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_delete_cookies_in_context(int context_handle, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char *")] sbyte* name);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_request_context_completion_callback(delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_delete_cookies_in_context_async(int context_handle, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const char *")] sbyte* name, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_flush_cookie_store_async(int context_handle, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_set_preference_async(int context_handle, sbyte* name, sbyte* value_json, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_browser_closed_callback([NativeTypeName("excef_browser_closed_cb_t")] delegate* unmanaged[Cdecl]<int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_cursor_change_callback([NativeTypeName("excef_cursor_change_cb_t")] delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_console_message_callback([NativeTypeName("excef_console_message_cb_t")] delegate* unmanaged[Cdecl]<int, int, sbyte*, sbyte*, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_load_start_callback([NativeTypeName("excef_load_start_cb_t")] delegate* unmanaged[Cdecl]<int, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_load_end_callback([NativeTypeName("excef_load_end_cb_t")] delegate* unmanaged[Cdecl]<int, int, sbyte*, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_load_error_callback([NativeTypeName("excef_load_error_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_loading_progress_callback([NativeTypeName("excef_loading_progress_cb_t")] delegate* unmanaged[Cdecl]<int, double, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_status_message_callback([NativeTypeName("excef_status_message_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_tooltip_callback([NativeTypeName("excef_tooltip_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_favicon_callback([NativeTypeName("excef_favicon_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_fullscreen_callback([NativeTypeName("excef_fullscreen_cb_t")] delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_exit_fullscreen(int browser_id, int will_cause_resize);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_browser_initialized_callback([NativeTypeName("excef_browser_initialized_cb_t")] delegate* unmanaged[Cdecl]<int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_scroll_offset_callback([NativeTypeName("excef_scroll_offset_cb_t")] delegate* unmanaged[Cdecl]<int, double, double, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_auto_resize_callback([NativeTypeName("excef_auto_resize_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_auto_resize_enabled(int browser_id, int enabled, int min_w, int min_h, int max_w, int max_h);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_can_zoom(int browser_id, int command);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_js_dialog_callback([NativeTypeName("excef_js_dialog_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_js_dialog([NativeTypeName("uint64_t")] ulong token, int success, [NativeTypeName("const char *")] sbyte* user_input);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_file_dialog_callback([NativeTypeName("excef_file_dialog_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, sbyte*, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_file_dialog([NativeTypeName("uint64_t")] ulong token, [NativeTypeName("const char *")] sbyte* paths);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_context_menu_callback([NativeTypeName("excef_context_menu_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_context_menu([NativeTypeName("uint64_t")] ulong token, int command_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_download_starting_callback([NativeTypeName("excef_download_starting_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, sbyte*, sbyte*, sbyte*, long, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_download_starting([NativeTypeName("uint64_t")] ulong token, [NativeTypeName("const char *")] sbyte* path, int show_dialog);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_download_progress_callback([NativeTypeName("excef_download_progress_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, int, long, long, long, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_download_action([NativeTypeName("uint64_t")] ulong token, int action);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_auth_request_callback_v2([NativeTypeName("excef_auth_request_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, sbyte*, int, sbyte*, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_auth([NativeTypeName("uint64_t")] ulong token, [NativeTypeName("const char *")] sbyte* username, [NativeTypeName("const char *")] sbyte* password);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_find_result_callback([NativeTypeName("excef_find_result_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_find(int browser_id, [NativeTypeName("const char *")] sbyte* search_text, int forward, int match_case, int find_next);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_stop_finding(int browser_id, int clear_selection);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_init_settings([NativeTypeName("const excef_init_settings *")] excef_init_settings* settings);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_cert_error_callback([NativeTypeName("excef_cert_error_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, int, sbyte*, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_cert_error([NativeTypeName("uint64_t")] ulong token, int proceed);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_before_popup_callback([NativeTypeName("excef_before_popup_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, sbyte*, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_host_popup_callback(delegate* unmanaged[Cdecl]<int, int, sbyte*, sbyte*, int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_permission_prompt_callback([NativeTypeName("excef_permission_prompt_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, ulong, sbyte*, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_permission_prompt([NativeTypeName("uint64_t")] ulong token, int result);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_media_access_callback([NativeTypeName("excef_media_access_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_media_access([NativeTypeName("uint64_t")] ulong token, int granted_permissions);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_render_process_gone_callback([NativeTypeName("excef_render_process_gone_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_register_custom_scheme([NativeTypeName("const char *")] sbyte* scheme_name, int is_standard, int is_local, int is_display_isolated, int is_secure, int is_cors_enabled, int is_csp_bypassing);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_scheme_request_callback([NativeTypeName("excef_scheme_request_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_scheme_request([NativeTypeName("uint64_t")] ulong token, int status_code, [NativeTypeName("const char *")] sbyte* status_text, [NativeTypeName("const char *")] sbyte* mime_type, [NativeTypeName("const unsigned char *")] byte* body, int body_length);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_resource_request_callback([NativeTypeName("excef_resource_request_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, sbyte*, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_resource_request([NativeTypeName("uint64_t")] ulong token, int action, [NativeTypeName("const char *")] sbyte* new_headers);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_popup_show_callback([NativeTypeName("excef_popup_show_cb_t")] delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_popup_size_callback([NativeTypeName("excef_popup_size_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_popup_paint_callback([NativeTypeName("excef_popup_paint_cb_t")] delegate* unmanaged[Cdecl]<int, void*, int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_js_invoke_callback([NativeTypeName("excef_js_invoke_cb_t")] delegate* unmanaged[Cdecl]<int, ulong, sbyte*, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_resolve_js_invoke([NativeTypeName("uint64_t")] ulong token, int success, [NativeTypeName("const char *")] sbyte* result_json);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_load_request(int browser_id, [NativeTypeName("const char *")] sbyte* method, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("const unsigned char *")] byte* post_body, int post_length, [NativeTypeName("const char *")] sbyte* headers_string);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_nav_entry_callback([NativeTypeName("excef_nav_entry_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, sbyte*, sbyte*, sbyte*, sbyte*, int, int, long, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_navigation_entries(int browser_id, int request_id, int current_only);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_string_visitor_callback([NativeTypeName("excef_string_visitor_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_frame_source(int browser_id, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_get_frame_text(int browser_id, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_load_string(int browser_id, [NativeTypeName("const char *")] sbyte* html);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_add_command_line_switch([NativeTypeName("const char *")] sbyte* name, [NativeTypeName("const char *")] sbyte* value);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_send_devtools_message(int browser_id, [NativeTypeName("const char *")] sbyte* message_json, int message_length);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_execute_devtools_method(int browser_id, int message_id, [NativeTypeName("const char *")] sbyte* method, [NativeTypeName("const char *")] sbyte* params_json);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_devtools_message_callback([NativeTypeName("excef_devtools_message_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_take_focus_callback([NativeTypeName("excef_take_focus_cb_t")] delegate* unmanaged[Cdecl]<int, int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_set_focus_callback([NativeTypeName("excef_set_focus_cb_t")] delegate* unmanaged[Cdecl]<int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_got_focus_callback([NativeTypeName("excef_got_focus_cb_t")] delegate* unmanaged[Cdecl]<int, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_pre_key_callback([NativeTypeName("excef_pre_key_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_key_event_callback([NativeTypeName("excef_key_event_cb_t")] delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_accessibility_tree_callback([NativeTypeName("excef_accessibility_tree_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_accessibility_location_callback([NativeTypeName("excef_accessibility_location_cb_t")] delegate* unmanaged[Cdecl]<int, sbyte*, void> cb);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_set_accessibility_enabled(int browser_id, int enabled);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_create_request_context([NativeTypeName("const char *")] sbyte* cache_path);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_release_request_context(int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_set_preference(int context_handle, [NativeTypeName("const char *")] sbyte* name, [NativeTypeName("const char *")] sbyte* value_json);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("const char *")]
    public static extern sbyte* excef_get_preference(int context_handle, [NativeTypeName("const char *")] sbyte* name);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_free_string([NativeTypeName("const char *")] sbyte* s);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_clear_http_auth_credentials(int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_close_all_connections(int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_clear_http_auth_credentials_async(int context_handle, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_close_all_connections_async(int context_handle, int request_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_create_offscreen_browser_in_context(int width, int height, float device_scale_factor, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("excef_paint_callback_t")] delegate* unmanaged[Cdecl]<int, void*, int, int, void> paint, int context_handle);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_create_offscreen_browser_ex(int width, int height, float device_scale_factor, [NativeTypeName("const char *")] sbyte* url, [NativeTypeName("excef_paint_callback_t")] delegate* unmanaged[Cdecl]<int, void*, int, int, void> paint, int context_handle, int flags);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_ime_set_composition(int browser_id, [NativeTypeName("const char *")] sbyte* text, int replacement_range_from, int replacement_range_length, int selection_range_from, int selection_range_length);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_ime_commit_text(int browser_id, [NativeTypeName("const char *")] sbyte* text, int replacement_range_from, int replacement_range_length, int relative_cursor_pos);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_ime_finish_composing(int browser_id, int keep_selection);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void excef_ime_cancel(int browser_id);

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int excef_print_to_pdf(int browser_id, [NativeTypeName("const char *")] sbyte* path, [NativeTypeName("excef_pdf_done_callback_t")] delegate* unmanaged[Cdecl]<int, int, void> callback);
}
