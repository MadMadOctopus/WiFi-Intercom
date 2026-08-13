# Top-level ESP-IDF project file.
# Build with:  idf.py set-target esp32c3 && idf.py build flash monitor
cmake_minimum_required(VERSION 3.16)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(wifi_intercom VERSION 0.6.1)
