/*
 * Linux x86-64 peer-thread patch helper for RedBird.
 *
 * This translation unit deliberately has no libc dependency. Once peer threads are
 * parked they may be holding arbitrary libc/loader locks, so every operation in the
 * dangerous interval is a raw system call, a lock-free atomic, or a byte loop.
 */

typedef unsigned char rb_u8;
typedef unsigned short rb_u16;
typedef unsigned int rb_u32;
typedef signed int rb_i32;
typedef unsigned long rb_u64;
typedef signed long rb_i64;
typedef unsigned long rb_size;

#define RB_EXPORT __attribute__((visibility("default")))
#define RB_NORETURN __attribute__((noreturn))

#define RB_SYS_READ 0
#define RB_SYS_CLOSE 3
#define RB_SYS_MPROTECT 10
#define RB_SYS_RT_SIGACTION 13
#define RB_SYS_RT_SIGRETURN 15
#define RB_SYS_GETPID 39
#define RB_SYS_GETTID 186
#define RB_SYS_FUTEX 202
#define RB_SYS_GETDENTS64 217
#define RB_SYS_CLOCK_GETTIME 228
#define RB_SYS_TGKILL 234
#define RB_SYS_OPENAT 257

#define RB_AT_FDCWD -100
#define RB_O_RDONLY 0
#define RB_O_DIRECTORY 00200000
#define RB_O_CLOEXEC 02000000
#define RB_PROT_READ 1
#define RB_PROT_WRITE 2
#define RB_FUTEX_WAIT_PRIVATE 128
#define RB_FUTEX_WAKE_PRIVATE 129
#define RB_CLOCK_MONOTONIC 1
#define RB_SA_SIGINFO 0x00000004UL
#define RB_SA_RESTORER 0x04000000UL
#define RB_SA_RESTART 0x10000000UL
#define RB_SIG_DFL ((void *)0)
#define RB_MAX_RT_SIGNAL 64
#define RB_MIN_USABLE_RT_SIGNAL 34
#define RB_KERNEL_SIGSET_SIZE 8
#define RB_MAX_THREADS 65536
#define RB_INT_MAX 2147483647

#define RB_EINTR 4
#define RB_ESRCH 3
#define RB_EAGAIN 11
#define RB_ENOMEM 12
#define RB_EBUSY 16
#define RB_EINVAL 22
#define RB_ETIMEDOUT 110

#define RB_STAGE_NONE 0
#define RB_STAGE_INSTALL_BOUNDARY 3
#define RB_STAGE_UNINSTALL_BOUNDARY 5
#define RB_STAGE_PROTECT 6
#define RB_STAGE_RESTORE_PROTECTION 8
#define RB_STAGE_SIGNAL 9
#define RB_STAGE_SIGNAL_BLOCKED 10
#define RB_STAGE_TIMEOUT 11
#define RB_STAGE_ENUMERATE 12

struct rb_timespec
{
    rb_i64 tv_sec;
    rb_i64 tv_nsec;
};

struct rb_kernel_sigaction
{
    void (*handler)(int, void *, void *);
    rb_u64 flags;
    void (*restorer)(void);
    rb_u64 mask;
};

struct rb_linux_dirent64
{
    rb_u64 inode;
    rb_i64 offset;
    rb_u16 record_length;
    rb_u8 type;
    char name[];
};

struct rb_memory_region
{
    rb_u64 address;
    rb_u64 length;
    rb_u32 protection;
    rb_u32 reserved;
};

struct rb_patch_result
{
    rb_i32 suspended_count;
    rb_i32 relocated_count;
    rb_i32 error_stage;
    rb_i32 native_error;
    rb_i32 signal_number;
    rb_i32 reserved;
    rb_u64 error_address;
};

struct rb_thread_slot
{
    rb_i32 tid;
    rb_i32 entered;
};

struct rb_patch_state
{
    struct rb_thread_slot *slots;
    rb_i32 slot_capacity;
    rb_i32 slot_count;
    rb_i32 release;
    rb_i32 handler_references;
    rb_i32 arrived_count;
    rb_i32 relocated_count;
    rb_i32 error_stage;
    rb_i32 native_error;
    rb_u64 error_address;
    const rb_u64 *source_instruction_pointers;
    const rb_u64 *relocated_instruction_pointers;
    rb_i32 instruction_count;
    rb_i32 source_length;
    rb_i32 installing;
    rb_u64 source_address;
};

static struct rb_patch_state rb_shared_state;
static struct rb_thread_slot rb_shared_slots[RB_MAX_THREADS];
static rb_i32 rb_active_generation;
static rb_i32 rb_next_generation;
static rb_i32 rb_call_lock;
static rb_i32 rb_signal_init_state;
static rb_i32 rb_patch_signal;

static inline rb_i64 rb_syscall6(rb_i64 number, rb_i64 argument1, rb_i64 argument2,
                                 rb_i64 argument3, rb_i64 argument4, rb_i64 argument5,
                                 rb_i64 argument6)
{
    register rb_i64 r10 __asm__("r10") = argument4;
    register rb_i64 r8 __asm__("r8") = argument5;
    register rb_i64 r9 __asm__("r9") = argument6;
    rb_i64 result;

    __asm__ volatile(
        "syscall"
        : "=a"(result)
        : "a"(number), "D"(argument1), "S"(argument2), "d"(argument3),
          "r"(r10), "r"(r8), "r"(r9)
        : "rcx", "r11", "memory");

    return result;
}

static inline rb_i64 rb_syscall0(rb_i64 number)
{
    return rb_syscall6(number, 0, 0, 0, 0, 0, 0);
}

static inline rb_i64 rb_syscall1(rb_i64 number, rb_i64 argument1)
{
    return rb_syscall6(number, argument1, 0, 0, 0, 0, 0);
}

static inline rb_i64 rb_syscall2(rb_i64 number, rb_i64 argument1, rb_i64 argument2)
{
    return rb_syscall6(number, argument1, argument2, 0, 0, 0, 0);
}

static inline rb_i64 rb_syscall3(rb_i64 number, rb_i64 argument1, rb_i64 argument2,
                                 rb_i64 argument3)
{
    return rb_syscall6(number, argument1, argument2, argument3, 0, 0, 0);
}

static inline rb_i64 rb_syscall4(rb_i64 number, rb_i64 argument1, rb_i64 argument2,
                                 rb_i64 argument3, rb_i64 argument4)
{
    return rb_syscall6(number, argument1, argument2, argument3, argument4, 0, 0);
}

static inline rb_i64 rb_atomic_load_i32(const rb_i32 *value)
{
    return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}

static inline void rb_atomic_store_i32(rb_i32 *value, rb_i32 replacement)
{
    __atomic_store_n(value, replacement, __ATOMIC_RELEASE);
}

static inline rb_i32 rb_atomic_load_generation(void)
{
    return __atomic_load_n(&rb_active_generation, __ATOMIC_ACQUIRE);
}

static rb_i32 rb_atomic_compare_exchange_i32(rb_i32 *value, rb_i32 expected,
                                             rb_i32 replacement)
{
    return __atomic_compare_exchange_n(value, &expected, replacement, 0,
                                       __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
}

static rb_i32 rb_futex_wait(rb_i32 *address, rb_i32 expected,
                            const struct rb_timespec *timeout)
{
    return (rb_i32)rb_syscall6(RB_SYS_FUTEX, (rb_i64)address,
                               RB_FUTEX_WAIT_PRIVATE, expected,
                               (rb_i64)timeout, 0, 0);
}

static void rb_futex_wake(rb_i32 *address, rb_i32 count)
{
    (void)rb_syscall6(RB_SYS_FUTEX, (rb_i64)address,
                      RB_FUTEX_WAKE_PRIVATE, count, 0, 0, 0);
}

__attribute__((naked)) static void rb_signal_restorer(void)
{
    __asm__ volatile(
        "mov $15, %rax\n"
        "syscall\n");
}

static void rb_record_error(struct rb_patch_state *state, rb_i32 stage,
                            rb_i32 native_error, rb_u64 address)
{
    if (rb_atomic_compare_exchange_i32(&state->error_stage, RB_STAGE_NONE, stage))
    {
        __atomic_store_n(&state->native_error, native_error, __ATOMIC_RELEASE);
        __atomic_store_n(&state->error_address, address, __ATOMIC_RELEASE);
    }
}

static void rb_patch_signal_handler(int signal_number, void *signal_info,
                                    void *raw_context)
{
    struct rb_patch_state *state;
    struct rb_thread_slot *slot = (struct rb_thread_slot *)0;
    rb_i32 generation;
    rb_i32 tid;
    rb_i32 count;
    rb_i32 index;
    rb_u64 *general_registers;
    rb_u64 instruction_pointer;

    (void)signal_number;
    (void)signal_info;

    generation = rb_atomic_load_generation();
    if (generation == 0)
        return;

    state = &rb_shared_state;
    __atomic_add_fetch(&state->handler_references, 1, __ATOMIC_ACQ_REL);
    if (rb_atomic_load_generation() != generation)
    {
        __atomic_sub_fetch(&state->handler_references, 1, __ATOMIC_ACQ_REL);
        rb_futex_wake(&state->handler_references, RB_INT_MAX);
        return;
    }

    tid = (rb_i32)rb_syscall0(RB_SYS_GETTID);
    count = rb_atomic_load_i32(&state->slot_count);
    for (index = 0; index < count; index++)
    {
        if (state->slots[index].tid == tid &&
            rb_atomic_load_i32(&state->slots[index].entered) != 2)
        {
            slot = state->slots + index;
            break;
        }
    }

    if (slot != (struct rb_thread_slot *)0 &&
        rb_atomic_compare_exchange_i32(&slot->entered, 0, 1))
    {
        /* Linux x86-64 ucontext_t has uc_mcontext.gregs at byte offset 40;
           REG_RIP is general register 16. This is the kernel signal-frame ABI. */
        general_registers = (rb_u64 *)((rb_u8 *)raw_context + 40);
        instruction_pointer = general_registers[16];

        if (instruction_pointer >= state->source_address &&
            instruction_pointer - state->source_address < (rb_u64)state->source_length)
        {
            rb_i32 boundary_index = -1;

            for (index = 0; index < state->instruction_count; index++)
            {
                if (state->source_instruction_pointers[index] == instruction_pointer)
                {
                    boundary_index = index;
                    break;
                }
            }

            if (boundary_index < 0)
            {
                rb_record_error(
                    state,
                    state->installing ? RB_STAGE_INSTALL_BOUNDARY : RB_STAGE_UNINSTALL_BOUNDARY,
                    0,
                    instruction_pointer);
            }
            else if (state->installing)
            {
                rb_u64 relocated = state->relocated_instruction_pointers[boundary_index];
                if (relocated == 0)
                {
                    rb_record_error(state, RB_STAGE_INSTALL_BOUNDARY, 0,
                                    instruction_pointer);
                }
                else
                {
                    general_registers[16] = relocated;
                    __atomic_add_fetch(&state->relocated_count, 1, __ATOMIC_ACQ_REL);
                }
            }
        }

        __atomic_add_fetch(&state->arrived_count, 1, __ATOMIC_ACQ_REL);
        rb_futex_wake(&state->arrived_count, RB_INT_MAX);

        while (rb_atomic_load_i32(&state->release) == 0)
            (void)rb_futex_wait(&state->release, 0, (const struct rb_timespec *)0);
    }

    __atomic_sub_fetch(&state->handler_references, 1, __ATOMIC_ACQ_REL);
    rb_futex_wake(&state->handler_references, RB_INT_MAX);

}

static rb_i32 rb_parse_decimal_tid(const char *text)
{
    rb_i64 value = 0;
    rb_i32 digits = 0;

    while (*text >= '0' && *text <= '9')
    {
        value = value * 10 + (*text - '0');
        if (value > RB_INT_MAX)
            return -1;
        text++;
        digits++;
    }

    return digits > 0 && *text == '\0' ? (rb_i32)value : -1;
}

static rb_i32 rb_contains_tid(const struct rb_thread_slot *slots, rb_i32 count,
                              rb_i32 tid)
{
    rb_i32 index;
    for (index = 0; index < count; index++)
    {
        if (slots[index].tid == tid &&
            rb_atomic_load_i32(&slots[index].entered) != 2)
            return 1;
    }

    return 0;
}

static rb_i32 rb_enumerate_new_threads(struct rb_patch_state *state, rb_i32 self_tid,
                                       rb_i32 *added_count)
{
    static const char task_path[] = "/proc/self/task";
    rb_u8 buffer[16384];
    rb_i64 directory;
    rb_i32 added = 0;
    rb_i32 slot_count = rb_atomic_load_i32(&state->slot_count);

    directory = rb_syscall4(RB_SYS_OPENAT, RB_AT_FDCWD, (rb_i64)task_path,
                            RB_O_RDONLY | RB_O_DIRECTORY | RB_O_CLOEXEC, 0);
    if (directory < 0)
        return (rb_i32)-directory;

    for (;;)
    {
        rb_i64 byte_count = rb_syscall3(RB_SYS_GETDENTS64, directory,
                                        (rb_i64)buffer, sizeof(buffer));
        rb_i64 offset = 0;

        if (byte_count == 0)
            break;
        if (byte_count < 0)
        {
            (void)rb_syscall1(RB_SYS_CLOSE, directory);
            return (rb_i32)-byte_count;
        }

        while (offset < byte_count)
        {
            struct rb_linux_dirent64 *entry =
                (struct rb_linux_dirent64 *)(buffer + offset);
            rb_i32 tid;

            if (entry->record_length < 20 || offset + entry->record_length > byte_count)
            {
                (void)rb_syscall1(RB_SYS_CLOSE, directory);
                return RB_EINVAL;
            }

            tid = rb_parse_decimal_tid(entry->name);
            if (tid > 0 && tid != self_tid &&
                !rb_contains_tid(state->slots, slot_count, tid))
            {
                if (slot_count >= state->slot_capacity)
                {
                    (void)rb_syscall1(RB_SYS_CLOSE, directory);
                    return RB_ENOMEM;
                }

                state->slots[slot_count].tid = tid;
                state->slots[slot_count].entered = 0;
                slot_count++;
                added++;
            }

            offset += entry->record_length;
        }
    }

    (void)rb_syscall1(RB_SYS_CLOSE, directory);
    rb_atomic_store_i32(&state->slot_count, slot_count);
    *added_count = added;
    return 0;
}

static rb_i32 rb_append_decimal(char *destination, rb_i32 offset, rb_i32 value)
{
    char reversed[16];
    rb_i32 count = 0;

    do
    {
        reversed[count++] = (char)('0' + value % 10);
        value /= 10;
    }
    while (value != 0);

    while (count > 0)
        destination[offset++] = reversed[--count];

    return offset;
}

static rb_i32 rb_hex_value(char character)
{
    if (character >= '0' && character <= '9')
        return character - '0';
    if (character >= 'a' && character <= 'f')
        return character - 'a' + 10;
    if (character >= 'A' && character <= 'F')
        return character - 'A' + 10;
    return -1;
}

static rb_i32 rb_read_blocked_signals(rb_i32 tid, rb_u64 *blocked)
{
    static const char prefix[] = "/proc/self/task/";
    static const char suffix[] = "/status";
    static const char label[] = "SigBlk:";
    char path[64];
    char buffer[16384];
    rb_i32 path_length = 0;
    rb_i32 index;
    rb_i64 file;
    rb_i64 total = 0;

    for (index = 0; prefix[index] != '\0'; index++)
        path[path_length++] = prefix[index];
    path_length = rb_append_decimal(path, path_length, tid);
    for (index = 0; suffix[index] != '\0'; index++)
        path[path_length++] = suffix[index];
    path[path_length] = '\0';

    file = rb_syscall4(RB_SYS_OPENAT, RB_AT_FDCWD, (rb_i64)path,
                       RB_O_RDONLY | RB_O_CLOEXEC, 0);
    if (file < 0)
        return (rb_i32)-file;

    while (total < (rb_i64)sizeof(buffer) - 1)
    {
        rb_i64 read_count = rb_syscall3(RB_SYS_READ, file,
                                        (rb_i64)(buffer + total),
                                        sizeof(buffer) - 1 - total);
        if (read_count == 0)
            break;
        if (read_count < 0)
        {
            if (read_count == -RB_EINTR)
                continue;
            (void)rb_syscall1(RB_SYS_CLOSE, file);
            return (rb_i32)-read_count;
        }
        total += read_count;
    }

    (void)rb_syscall1(RB_SYS_CLOSE, file);
    buffer[total] = '\0';

    for (index = 0; index + 7 < total; index++)
    {
        rb_i32 label_index;
        rb_i32 cursor;
        rb_u64 value = 0;
        rb_i32 digits = 0;

        if (index != 0 && buffer[index - 1] != '\n')
            continue;

        for (label_index = 0; label[label_index] != '\0'; label_index++)
        {
            if (buffer[index + label_index] != label[label_index])
                break;
        }
        if (label[label_index] != '\0')
            continue;

        cursor = index + label_index;
        while (buffer[cursor] == ' ' || buffer[cursor] == '\t')
            cursor++;

        while (buffer[cursor] != '\0' && buffer[cursor] != '\n')
        {
            rb_i32 digit = rb_hex_value(buffer[cursor]);
            if (digit < 0)
                break;
            value = (value << 4) | (rb_u64)digit;
            digits++;
            cursor++;
        }

        if (digits == 0)
            return RB_EINVAL;

        *blocked = value;
        return 0;
    }

    return RB_EINVAL;
}

static rb_i32 rb_signal_blocked_by_any(const struct rb_patch_state *state,
                                       rb_i32 signal_number, rb_i32 *blocked_tid)
{
    rb_i32 index;
    rb_u64 signal_bit = 1UL << (signal_number - 1);

    for (index = 0; index < state->slot_count; index++)
    {
        rb_u64 blocked = 0;
        rb_i32 error = rb_read_blocked_signals(state->slots[index].tid, &blocked);

        if (error == RB_ESRCH)
            continue;
        if (error != 0)
            return error;
        if ((blocked & signal_bit) != 0)
        {
            *blocked_tid = state->slots[index].tid;
            return -1;
        }
    }

    return 0;
}

static rb_i32 rb_query_signal_action(rb_i32 signal_number,
                                     struct rb_kernel_sigaction *action)
{
    rb_i64 result = rb_syscall4(RB_SYS_RT_SIGACTION, signal_number, 0,
                                (rb_i64)action, RB_KERNEL_SIGSET_SIZE);
    return result < 0 ? (rb_i32)-result : 0;
}

static rb_i32 rb_install_signal_action(rb_i32 signal_number)
{
    struct rb_kernel_sigaction action;
    rb_i64 result;

    action.handler = rb_patch_signal_handler;
    action.flags = RB_SA_SIGINFO | RB_SA_RESTART | RB_SA_RESTORER;
    action.restorer = rb_signal_restorer;
    action.mask = ~0UL;

    result = rb_syscall4(RB_SYS_RT_SIGACTION, signal_number, (rb_i64)&action, 0,
                         RB_KERNEL_SIGSET_SIZE);
    return result < 0 ? (rb_i32)-result : 0;
}

static rb_i32 rb_ensure_patch_signal(const struct rb_patch_state *state,
                                     rb_i32 *blocked_tid)
{
    rb_i32 init_state = rb_atomic_load_i32(&rb_signal_init_state);

    if (init_state == 2)
    {
        struct rb_kernel_sigaction current;
        rb_i32 error = rb_query_signal_action(rb_patch_signal, &current);
        if (error != 0)
            return error;
        if ((void *)current.handler != (void *)rb_patch_signal_handler)
            return RB_EBUSY;
        return rb_signal_blocked_by_any(state, rb_patch_signal, blocked_tid);
    }

    if (!rb_atomic_compare_exchange_i32(&rb_signal_init_state, 0, 1))
    {
        while (rb_atomic_load_i32(&rb_signal_init_state) == 1)
            (void)rb_futex_wait(&rb_signal_init_state, 1,
                                (const struct rb_timespec *)0);
        return rb_ensure_patch_signal(state, blocked_tid);
    }

    for (rb_i32 candidate = RB_MAX_RT_SIGNAL;
         candidate >= RB_MIN_USABLE_RT_SIGNAL; candidate--)
    {
        struct rb_kernel_sigaction current;
        rb_i32 error = rb_query_signal_action(candidate, &current);

        if (error != 0 || (void *)current.handler != RB_SIG_DFL)
            continue;

        error = rb_signal_blocked_by_any(state, candidate, blocked_tid);
        if (error != 0)
        {
            if (error == -1)
                continue;
            rb_atomic_store_i32(&rb_signal_init_state, 0);
            rb_futex_wake(&rb_signal_init_state, RB_INT_MAX);
            return error;
        }

        error = rb_install_signal_action(candidate);
        if (error == 0)
        {
            rb_patch_signal = candidate;
            rb_atomic_store_i32(&rb_signal_init_state, 2);
            rb_futex_wake(&rb_signal_init_state, RB_INT_MAX);
            return 0;
        }
    }

    rb_atomic_store_i32(&rb_signal_init_state, 0);
    rb_futex_wake(&rb_signal_init_state, RB_INT_MAX);
    return RB_EBUSY;
}

static rb_i64 rb_monotonic_nanoseconds(void)
{
    struct rb_timespec time;
    rb_i64 result = rb_syscall2(RB_SYS_CLOCK_GETTIME, RB_CLOCK_MONOTONIC,
                                (rb_i64)&time);
    if (result < 0)
        return result;
    return time.tv_sec * 1000000000L + time.tv_nsec;
}

static rb_i32 rb_wait_for_arrivals(struct rb_patch_state *state, rb_i32 expected,
                                   rb_i64 deadline)
{
    while (rb_atomic_load_i32(&state->arrived_count) < expected)
    {
        struct rb_timespec remaining;
        rb_i64 now = rb_monotonic_nanoseconds();
        rb_i64 nanoseconds;
        rb_i32 observed;
        rb_i32 wait_result;

        if (now < 0)
            return (rb_i32)-now;
        if (now >= deadline)
            return RB_ETIMEDOUT;

        nanoseconds = deadline - now;
        remaining.tv_sec = nanoseconds / 1000000000L;
        remaining.tv_nsec = nanoseconds % 1000000000L;
        observed = rb_atomic_load_i32(&state->arrived_count);
        wait_result = rb_futex_wait(&state->arrived_count, observed, &remaining);

        if (wait_result < 0 && wait_result != -RB_EINTR &&
            wait_result != -RB_EAGAIN && wait_result != -RB_ETIMEDOUT)
            return -wait_result;
    }

    return 0;
}

static void rb_release_handlers(struct rb_patch_state *state)
{
    rb_atomic_store_i32(&state->release, 1);
    rb_futex_wake(&state->release, RB_INT_MAX);
    rb_atomic_store_i32(&rb_active_generation, 0);

    while (rb_atomic_load_i32(&state->handler_references) != 0)
    {
        rb_i32 observed = rb_atomic_load_i32(&state->handler_references);
        if (observed != 0)
            (void)rb_futex_wait(&state->handler_references, observed,
                                (const struct rb_timespec *)0);
    }
}

static rb_i32 rb_signal_new_slots(struct rb_patch_state *state, rb_i32 first_index,
                                  rb_i32 process_id, rb_i32 *expected)
{
    rb_i32 index;
    for (index = first_index; index < state->slot_count; index++)
    {
        rb_i32 tid = state->slots[index].tid;
        rb_i64 result = rb_syscall3(RB_SYS_TGKILL, process_id, tid, rb_patch_signal);

        if (result == -RB_ESRCH)
        {
            rb_atomic_store_i32(&state->slots[index].entered, 2);
            continue;
        }
        if (result < 0)
            return (rb_i32)-result;

        (*expected)++;
    }

    return 0;
}

static void rb_apply_patch_bytes(struct rb_patch_state *state,
                                 rb_u64 patch_address, const rb_u8 *patch_bytes,
                                 rb_i32 patch_length,
                                 const struct rb_memory_region *regions,
                                 rb_i32 region_count)
{
    rb_i32 changed_count = 0;
    rb_i32 index;

    for (; changed_count < region_count; changed_count++)
    {
        const struct rb_memory_region *region = regions + changed_count;
        rb_i64 result = rb_syscall3(RB_SYS_MPROTECT, region->address,
                                    region->length,
                                    RB_PROT_READ | RB_PROT_WRITE | 4);
        if (result < 0)
        {
            state->error_stage = RB_STAGE_PROTECT;
            state->native_error = (rb_i32)-result;
            state->error_address = region->address;
            break;
        }
    }

    if (state->error_stage == RB_STAGE_NONE)
    {
        volatile rb_u8 *destination = (volatile rb_u8 *)patch_address;
        for (index = 0; index < patch_length; index++)
            destination[index] = patch_bytes[index];
        __atomic_thread_fence(__ATOMIC_SEQ_CST);
    }

    for (index = changed_count - 1; index >= 0; index--)
    {
        const struct rb_memory_region *region = regions + index;
        rb_i64 result = rb_syscall3(RB_SYS_MPROTECT, region->address,
                                    region->length, region->protection);
        if (result < 0 && state->error_stage == RB_STAGE_NONE)
        {
            state->error_stage = RB_STAGE_RESTORE_PROTECTION;
            state->native_error = (rb_i32)-result;
            state->error_address = region->address;
        }
    }
}

RB_EXPORT rb_i32 rb_patch_apply(
    const rb_u64 *source_instruction_pointers,
    const rb_u64 *relocated_instruction_pointers,
    rb_i32 instruction_count,
    rb_u64 source_address,
    rb_i32 source_length,
    rb_i32 installing,
    rb_u64 patch_address,
    const rb_u8 *patch_bytes,
    rb_i32 patch_length,
    const struct rb_memory_region *regions,
    rb_i32 region_count,
    rb_i32 timeout_milliseconds,
    struct rb_patch_result *result)
{
    struct rb_patch_state *state_pointer = &rb_shared_state;
    struct rb_thread_slot *slots = rb_shared_slots;
    rb_i64 now;
    rb_i64 deadline;
    rb_i32 self_tid;
    rb_i32 process_id;
    rb_i32 added_count;
    rb_i32 blocked_tid = 0;
    rb_i32 expected = 0;
    rb_i32 error;

    if (result == (struct rb_patch_result *)0)
        return -1;

    result->suspended_count = 0;
    result->relocated_count = 0;
    result->error_stage = RB_STAGE_NONE;
    result->native_error = 0;
    result->signal_number = 0;
    result->reserved = 0;
    result->error_address = 0;

    if (source_instruction_pointers == (const rb_u64 *)0 ||
        relocated_instruction_pointers == (const rb_u64 *)0 ||
        patch_bytes == (const rb_u8 *)0 ||
        regions == (const struct rb_memory_region *)0 ||
        instruction_count <= 0 || source_address == 0 || source_length <= 0 ||
        patch_address == 0 || patch_length <= 0 || region_count <= 0 ||
        timeout_milliseconds <= 0)
    {
        result->error_stage = RB_STAGE_ENUMERATE;
        result->native_error = RB_EINVAL;
        return -1;
    }

    if (!rb_atomic_compare_exchange_i32(&rb_call_lock, 0, 1))
    {
        result->error_stage = RB_STAGE_SIGNAL;
        result->native_error = RB_EBUSY;
        return -1;
    }

#define state (*state_pointer)

    state.slots = slots;
    state.slot_capacity = RB_MAX_THREADS;
    state.slot_count = 0;
    state.release = 0;
    /* handler_references is process-lifetime storage. A very late handler from a
       previous generation may still increment it before observing the generation
       mismatch, so it must never be reset between calls. */
    state.arrived_count = 0;
    state.relocated_count = 0;
    state.error_stage = RB_STAGE_NONE;
    state.native_error = 0;
    state.error_address = 0;
    state.source_instruction_pointers = source_instruction_pointers;
    state.relocated_instruction_pointers = relocated_instruction_pointers;
    state.instruction_count = instruction_count;
    state.source_length = source_length;
    state.installing = installing != 0;
    state.source_address = source_address;

    self_tid = (rb_i32)rb_syscall0(RB_SYS_GETTID);
    process_id = (rb_i32)rb_syscall0(RB_SYS_GETPID);
    error = rb_enumerate_new_threads(&state, self_tid, &added_count);
    if (error != 0)
    {
        state.error_stage = RB_STAGE_ENUMERATE;
        state.native_error = error;
        goto complete;
    }

    error = rb_ensure_patch_signal(&state, &blocked_tid);
    if (error != 0)
    {
        state.error_stage = error == -1 ? RB_STAGE_SIGNAL_BLOCKED : RB_STAGE_SIGNAL;
        state.native_error = error == -1 ? 0 : error;
        state.error_address = (rb_u64)blocked_tid;
        goto complete;
    }

    result->signal_number = rb_patch_signal;
    now = rb_monotonic_nanoseconds();
    if (now < 0)
    {
        state.error_stage = RB_STAGE_ENUMERATE;
        state.native_error = (rb_i32)-now;
        goto complete;
    }
    deadline = now + (rb_i64)timeout_milliseconds * 1000000L;

    {
        rb_i32 generation = __atomic_add_fetch(&rb_next_generation, 1,
                                               __ATOMIC_ACQ_REL);
        if (generation <= 0)
        {
            generation = 1;
            rb_atomic_store_i32(&rb_next_generation, generation);
        }
        rb_atomic_store_i32(&rb_active_generation, generation);
    }

    {
        rb_i32 signalled_until = 0;
        for (;;)
        {
            error = rb_signal_blocked_by_any(&state, rb_patch_signal, &blocked_tid);
            if (error != 0)
            {
                rb_record_error(&state,
                                error == -1 ? RB_STAGE_SIGNAL_BLOCKED : RB_STAGE_SIGNAL,
                                error == -1 ? 0 : error,
                                (rb_u64)blocked_tid);
                break;
            }

            error = rb_signal_new_slots(&state, signalled_until, process_id, &expected);
            signalled_until = state.slot_count;
            if (error != 0)
            {
                rb_record_error(&state, RB_STAGE_SIGNAL, error, 0);
                break;
            }

            error = rb_wait_for_arrivals(&state, expected, deadline);
            if (error != 0)
            {
                rb_record_error(&state,
                                error == RB_ETIMEDOUT ? RB_STAGE_TIMEOUT : RB_STAGE_SIGNAL,
                                error == RB_ETIMEDOUT ? 0 : error,
                                0);
                break;
            }

            error = rb_enumerate_new_threads(&state, self_tid, &added_count);
            if (error != 0)
            {
                rb_record_error(&state, RB_STAGE_ENUMERATE, error, 0);
                break;
            }
            if (added_count == 0)
                break;
        }
    }

    if (state.error_stage == RB_STAGE_NONE)
        rb_apply_patch_bytes(&state, patch_address, patch_bytes, patch_length,
                             regions, region_count);

    rb_release_handlers(&state);

complete:
    result->suspended_count = rb_atomic_load_i32(&state.arrived_count);
    result->relocated_count = rb_atomic_load_i32(&state.relocated_count);
    result->error_stage = rb_atomic_load_i32(&state.error_stage);
    result->native_error = rb_atomic_load_i32(&state.native_error);
    result->error_address = __atomic_load_n(&state.error_address, __ATOMIC_ACQUIRE);
    if (result->signal_number == 0)
        result->signal_number = rb_patch_signal;

    rb_atomic_store_i32(&rb_call_lock, 0);
    rb_futex_wake(&rb_call_lock, RB_INT_MAX);
    rb_i32 return_value = result->error_stage == RB_STAGE_NONE ? 0 : -1;
#undef state
    return return_value;
}
