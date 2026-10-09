# AgentZero's watch: ESP-Brookesia phone apps (AskBot first) for the Waveshare ESP32-S3-Touch-AMOLED-1.75C.
# Build: idf.py set-target esp32s3 && idf.py -p COM7 flash monitor   (ESP-IDF v5.5.x)
cmake_minimum_required(VERSION 3.16)

# brookesia_core (44 MB) is reused from Waveshare's board repo instead of being vendored here.
# Clone https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-1.75C and point WS_AMOLED_REPO at it.
if(DEFINED ENV{WS_AMOLED_REPO})
    set(WS_AMOLED_REPO "$ENV{WS_AMOLED_REPO}")
else()
    set(WS_AMOLED_REPO "C:/esp/ws-amoled-175c")
endif()
set(EXTRA_COMPONENT_DIRS "${WS_AMOLED_REPO}/examples/esp-idf/03_esp-brookesia/components/brookesia_core")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(zero_wearable_device)
