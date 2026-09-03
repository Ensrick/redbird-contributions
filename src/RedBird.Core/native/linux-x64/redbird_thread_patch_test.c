#define _GNU_SOURCE
#include <dlfcn.h>
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <ucontext.h>
#include <unistd.h>

struct rb_memory_region
{
    uint64_t address;
    uint64_t length;
    uint32_t protection;
    uint32_t reserved;
};

struct rb_patch_result
{
    int32_t suspended_count;
    int32_t relocated_count;
    int32_t error_stage;
    int32_t native_error;
    int32_t signal_number;
    int32_t reserved;
    uint64_t error_address;
};

_Static_assert(sizeof(struct rb_memory_region) == 24, "managed/native region ABI changed");
_Static_assert(sizeof(struct rb_patch_result) == 32, "managed/native result ABI changed");
_Static_assert(offsetof(ucontext_t, uc_mcontext.gregs) == 40, "unexpected x64 ucontext ABI");
_Static_assert(REG_RIP == 16, "unexpected x64 REG_RIP index");

typedef int32_t (*rb_patch_apply_fn)(
    const uint64_t *, const uint64_t *, int32_t, uint64_t, int32_t, int32_t,
    uint64_t, const uint8_t *, int32_t, const struct rb_memory_region *, int32_t,
    int32_t, struct rb_patch_result *);

typedef int (*target_fn)(void);

static target_fn target;
static int worker_result;
static int blocked_signal;
static int blocked_ready;
static int blocked_stop;

static void *run_target(void *unused)
{
    (void)unused;
    worker_result = target();
    return NULL;
}

static void *run_with_patch_signal_blocked(void *unused)
{
    sigset_t signals;
    (void)unused;

    sigemptyset(&signals);
    sigaddset(&signals, blocked_signal);
    if (pthread_sigmask(SIG_BLOCK, &signals, NULL) != 0)
    {
        __atomic_store_n(&blocked_ready, -1, __ATOMIC_RELEASE);
        return (void *)1;
    }

    __atomic_store_n(&blocked_ready, 1, __ATOMIC_RELEASE);
    while (__atomic_load_n(&blocked_stop, __ATOMIC_ACQUIRE) == 0)
        __asm__ volatile("pause");
    return NULL;
}

int main(int argc, char **argv)
{
    const size_t page_size = (size_t)sysconf(_SC_PAGESIZE);
    uint8_t *page;
    uint8_t *trampoline;
    volatile int32_t *gate;
    volatile int32_t *counter;
    pthread_t worker;
    void *library;
    rb_patch_apply_fn apply;
    uint64_t source_ips[4];
    uint64_t relocated_ips[4];
    struct rb_memory_region region;
    struct rb_patch_result result;
    uint8_t patch[17];
    int index;
    int successful_suspended;
    int successful_relocated;

    if (argc != 2)
        return 2;

    library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (library == NULL)
    {
        fprintf(stderr, "dlopen: %s\n", dlerror());
        return 3;
    }
    apply = (rb_patch_apply_fn)dlsym(library, "rb_patch_apply");
    if (apply == NULL)
        return 4;

    page = mmap(NULL, page_size, PROT_READ | PROT_WRITE | PROT_EXEC,
                MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (page == MAP_FAILED)
        return 5;

    gate = (volatile int32_t *)(page + 128);
    counter = (volatile int32_t *)(page + 132);
    trampoline = page + 256;

    /* inc [counter]; mov eax,[gate]; test eax,eax; je loop; mov eax,7; ret */
    {
        const uint8_t original[] = {
            0x83, 0x05, 0x7D, 0x00, 0x00, 0x00, 0x01,
            0x8B, 0x05, 0x73, 0x00, 0x00, 0x00,
            0x85, 0xC0,
            0x74, 0xEF,
            0xB8, 0x07, 0x00, 0x00, 0x00,
            0xC3,
        };
        const uint8_t relocated[] = {
            0x83, 0x05, 0x7D, 0xFF, 0xFF, 0xFF, 0x01,
            0x8B, 0x05, 0x73, 0xFF, 0xFF, 0xFF,
            0x85, 0xC0,
            0x74, 0xEF,
            0xB8, 0x07, 0x00, 0x00, 0x00,
            0xC3,
        };
        memcpy(page, original, sizeof(original));
        memcpy(trampoline, relocated, sizeof(relocated));
    }

    *gate = 0;
    *counter = 0;
    __builtin___clear_cache((char *)page, (char *)page + page_size);
    target = (target_fn)page;

    if (pthread_create(&worker, NULL, run_target, NULL) != 0)
        return 6;
    for (index = 0; index < 1000000 && *counter == 0; index++)
        __asm__ volatile("pause");
    if (*counter == 0)
        return 7;

    source_ips[0] = (uint64_t)(page + 0);
    source_ips[1] = (uint64_t)(page + 7);
    source_ips[2] = (uint64_t)(page + 13);
    source_ips[3] = (uint64_t)(page + 15);
    relocated_ips[0] = (uint64_t)(trampoline + 0);
    relocated_ips[1] = (uint64_t)(trampoline + 7);
    relocated_ips[2] = (uint64_t)(trampoline + 13);
    relocated_ips[3] = (uint64_t)(trampoline + 15);

    patch[0] = 0xB8;
    patch[1] = 99;
    patch[2] = patch[3] = patch[4] = 0;
    patch[5] = 0xC3;
    memset(patch + 6, 0x90, sizeof(patch) - 6);

    region.address = (uint64_t)page;
    region.length = page_size;
    region.protection = PROT_READ | PROT_WRITE | PROT_EXEC;
    region.reserved = 0;

    if (apply(source_ips, relocated_ips, 4, (uint64_t)page, 17, 1,
              (uint64_t)page, patch, sizeof(patch), &region, 1, 5000, &result) != 0)
    {
        fprintf(stderr, "stage=%d errno=%d signal=%d address=%#lx suspended=%d\n",
                result.error_stage, result.native_error, result.signal_number,
                (unsigned long)result.error_address, result.suspended_count);
        *gate = 1;
        pthread_join(worker, NULL);
        return 8;
    }

    *gate = 1;
    pthread_join(worker, NULL);
    if (worker_result != 7 || target() != 99 ||
        result.suspended_count < 1 || result.relocated_count != 1)
    {
        fprintf(stderr, "worker=%d target=%d suspended=%d relocated=%d\n",
                worker_result, target(), result.suspended_count,
                result.relocated_count);
        return 9;
    }
    successful_suspended = result.suspended_count;
    successful_relocated = result.relocated_count;

    /* A blocked patch signal must be detected before the first byte changes. */
    blocked_signal = result.signal_number;
    blocked_ready = 0;
    blocked_stop = 0;
    if (pthread_create(&worker, NULL, run_with_patch_signal_blocked, NULL) != 0)
        return 10;
    while (__atomic_load_n(&blocked_ready, __ATOMIC_ACQUIRE) == 0)
        __asm__ volatile("pause");
    if (__atomic_load_n(&blocked_ready, __ATOMIC_ACQUIRE) < 0)
    {
        pthread_join(worker, NULL);
        return 11;
    }

    patch[0] = 0x90;
    memset(&result, 0, sizeof(result));
    if (apply(source_ips, relocated_ips, 4, (uint64_t)page, 17, 1,
              (uint64_t)page, patch, sizeof(patch), &region, 1, 5000, &result) == 0 ||
        result.error_stage != 10 || page[0] != 0xB8)
    {
        fprintf(stderr, "blocked-signal stage=%d errno=%d first-byte=%#x\n",
                result.error_stage, result.native_error, page[0]);
        __atomic_store_n(&blocked_stop, 1, __ATOMIC_RELEASE);
        pthread_join(worker, NULL);
        return 12;
    }

    __atomic_store_n(&blocked_stop, 1, __ATOMIC_RELEASE);
    if (pthread_join(worker, NULL) != 0)
        return 13;

    printf("ok signal=%d suspended=%d relocated=%d blocked-signal-rejected=yes\n",
           result.signal_number, successful_suspended, successful_relocated);
    return 0;
}
