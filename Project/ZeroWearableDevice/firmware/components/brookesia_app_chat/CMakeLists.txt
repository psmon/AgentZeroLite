set(PROJ_SRC ${CMAKE_CURRENT_LIST_DIR})

file(GLOB PROJ_SRCS_C   ${PROJ_SRC}/*.c ${PROJ_SRC}/assets/*.c)
file(GLOB PROJ_SRCS_CPP ${PROJ_SRC}/*.cpp)

# Shares the NUS transport (hud_ble.cpp) and the NanumGothic font with brookesia_app_claude_hud.
idf_component_register(
    SRCS ${PROJ_SRCS_C} ${PROJ_SRCS_CPP}
    INCLUDE_DIRS ${PROJ_SRC}
    REQUIRES brookesia_core brookesia_app_claude_hud json bt
    WHOLE_ARCHIVE
)

set_source_files_properties(
    ${PROJ_SRCS_CPP}
    PROPERTIES
        COMPILE_FLAGS "-Wno-missing-field-initializers"
)
