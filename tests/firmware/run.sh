#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${IDF_PATH:?Set IDF_PATH to ESP-IDF 5.4.x (provides the production cJSON source)}"
test_build=$(mktemp -d)
trap 'rm -rf "$test_build"' EXIT
cc -std=c11 -Wall -Wextra -Werror -Wno-unused-parameter -fsanitize=address,undefined -g \
  -Itests/firmware/stubs -Ifirmware/main -I"$IDF_PATH/components/json/cJSON" \
  tests/firmware/test_main.c firmware/main/protocol.c \
  "$IDF_PATH/components/json/cJSON/cJSON.c" -lm -pthread -o "$test_build/firmware-tests"
"$test_build/firmware-tests"
