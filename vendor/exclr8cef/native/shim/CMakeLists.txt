# Exclr8CEF shim — shared library exposing a C ABI on top of CEF.
#
# Layout:
#   exclr8cef                  - shared library, the C ABI surface
#   exclr8cef_version_probe    - Stage 1 smoke test (no CEF init)

set(SHIM_LIB exclr8cef)
set(SHIM_TEST exclr8cef_version_probe)

set(SHIM_SRCS
    exclr8cef.h
    exclr8cef.cc
    exclr8cef_app.h
    exclr8cef_app.cc
    exclr8cef_client.h
    exclr8cef_client.cc
    exclr8cef_osr.h
    exclr8cef_osr.cc
    # Optional PrintToPDF extension. The header (exclr8cef_print.h) is
    # opt-in for consumers; the .cc is compiled into the same library
    # so apps that need it just include the header + import the
    # matching managed package.
    exclr8cef_print.h
    exclr8cef_print.cc
)
if(OS_MAC)
    list(APPEND SHIM_SRCS exclr8cef_mac.mm)
elseif(OS_WINDOWS)
    list(APPEND SHIM_SRCS exclr8cef_win.cc)
endif()

# ---- Shared library: the C ABI surface ------------------------------------
add_library(${SHIM_LIB} SHARED ${SHIM_SRCS})
SET_LIBRARY_TARGET_PROPERTIES(${SHIM_LIB})
add_dependencies(${SHIM_LIB} libcef_dll_wrapper)

# Windows sandbox_info must be created by (or supplied to) the process entry
# executable, never by a DLL. CEF 138+ additionally requires bootstrap[c].exe
# to call a native entry DLL before the CLR starts. The current .NET-hosted C
# ABI cannot satisfy either contract. Refuse the default secure configuration
# instead of silently producing a binary that only appears sandboxed.
if(OS_WINDOWS AND USE_SANDBOX)
    list(FIND CEF_COMPILER_DEFINES "CEF_USE_BOOTSTRAP" _bootstrap_define)
    if(NOT _bootstrap_define EQUAL -1)
        message(FATAL_ERROR
            "CEF 138+ Windows sandbox requires bootstrap[c].exe and a native "
            "entry DLL. Exclr8CEF's .NET-hosted shim does not yet implement "
            "that launcher contract. Configure explicitly with "
            "-DUSE_SANDBOX=OFF for an unsandboxed development build.")
    else()
        message(FATAL_ERROR
            "Windows sandbox_info must be created in the process entry "
            "executable, not Exclr8CEF's .NET-loaded shim DLL. Configure "
            "explicitly with -DUSE_SANDBOX=OFF for an unsandboxed "
            "development build, or provide a native launcher contract.")
    endif()
endif()

target_include_directories(${SHIM_LIB}
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
    PRIVATE "${CEF_ROOT}"
)

target_compile_definitions(${SHIM_LIB} PRIVATE EXCLR8CEF_BUILDING)

set_target_properties(${SHIM_LIB} PROPERTIES
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

if(OS_MAC)
    set_source_files_properties(exclr8cef_mac.mm PROPERTIES
        COMPILE_FLAGS "-fobjc-arc"
    )
    set_target_properties(${SHIM_LIB} PROPERTIES
        MACOSX_RPATH ON
        INSTALL_NAME_DIR "@rpath"
    )
endif()

# GCC 11+ on Linux promotes strncpy "output may be truncated" to an error
# under CEF's -Werror, but our copy_to() helper null-terminates explicitly.
# Suppress narrowly on the shim target.
if(UNIX AND NOT APPLE)
    target_compile_options(${SHIM_LIB} PRIVATE -Wno-stringop-truncation)
endif()

# On macOS the CEF framework is loaded at runtime via CefScopedLibraryLoader,
# so we link only libcef_dll_wrapper — NOT libcef_lib.
if(OS_MAC)
    target_link_libraries(${SHIM_LIB} PRIVATE
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
        "-framework CoreVideo"
        "-framework IOSurface"
        "-framework Metal"
        "-framework Security"
    )
else()
    target_link_libraries(${SHIM_LIB} PRIVATE
        libcef_lib
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
    )
endif()

# ---- Stage 1 smoke test ---------------------------------------------------
add_executable(${SHIM_TEST} version_probe.c)
target_link_libraries(${SHIM_TEST} PRIVATE ${SHIM_LIB})

if(OS_MAC)
    set_target_properties(${SHIM_TEST} PROPERTIES
        INSTALL_RPATH "@executable_path"
        BUILD_WITH_INSTALL_RPATH TRUE
    )
endif()

add_custom_command(TARGET ${SHIM_TEST} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:${SHIM_LIB}>
        $<TARGET_FILE_DIR:${SHIM_TEST}>
)
