#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir="$script_dir/../../runtimes/linux-x64/native"
compiler=${CC:-cc}

mkdir -p "$output_dir"

"$compiler" \
  -shared -fPIC -O2 -std=gnu11 \
  -ffreestanding -fno-builtin -fno-stack-protector \
  -fvisibility=hidden -nostdlib \
  -Wall -Wextra -Werror \
  -Wl,-z,defs \
  -Wl,-soname,libredbird_thread_patch.so \
  -o "$output_dir/libredbird_thread_patch.so" \
  "$script_dir/redbird_thread_patch.c"

echo "Built $output_dir/libredbird_thread_patch.so"
