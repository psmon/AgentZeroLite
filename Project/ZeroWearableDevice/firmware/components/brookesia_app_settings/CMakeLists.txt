set(PROJ_SRC ${CMAKE_CURRENT_LIST_DIR})

file(GLOB PROJ_SRCS_C   ${PROJ_SRC}/*.c ${PROJ_SRC}/assets/*.c)
file(GLOB PROJ_SRCS_CPP ${PROJ_SRC}/*.cpp)

# Reads and writes the audio settings owned by brookesia_app_chat's Core (it holds the
# codec handles), and owns the device-wide WiFi station service (device_wifi.*).
idf_component_register(
    SRCS ${PROJ_SRCS_C} ${PROJ_SRCS_CPP}
    INCLUDE_DIRS ${PROJ_SRC}
    REQUIRES brookesia_core brookesia_app_chat esp_wifi esp_netif nvs_flash esp_event
    WHOLE_ARCHIVE
)

set_source_files_properties(
    ${PROJ_SRCS_CPP}
    PROPERTIES
        COMPILE_FLAGS "-Wno-missing-field-initializers"
)
