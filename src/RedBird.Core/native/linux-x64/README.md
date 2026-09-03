# Linux x64 thread-patch companion

`redbird_thread_patch.c` implements the only part of protected Linux patching that cannot safely run in managed code: receiving an asynchronous signal on an arbitrary peer thread, relocating its saved instruction pointer, parking it on a futex, changing page protections, and copying the patch while every peer is stopped.

The helper links no C runtime or threading library. After peer threads are parked they may own arbitrary runtime, loader, allocator, or I/O locks, so the helper uses only raw x86-64 Linux system calls, lock-free atomics, and byte loops. `build.sh` reproduces the packaged `libredbird_thread_patch.so` with GCC or Clang.

The signal handler remains installed for the process lifetime. This makes a signal delivered after a timed-out patch harmless. Before every patch the helper verifies that its signal disposition is unchanged and that no peer thread blocks the signal. It fails without writing when either check fails.

Run the native integration test on a normal x86-64 Linux host with `/proc` mounted:

```sh
sh ./build.sh
cc -O2 -Wall -Wextra -Werror -pthread -ldl \
  -o redbird_thread_patch_test redbird_thread_patch_test.c
./redbird_thread_patch_test ../../runtimes/linux-x64/native/libredbird_thread_patch.so
```
