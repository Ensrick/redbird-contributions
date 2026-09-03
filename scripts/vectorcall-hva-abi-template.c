// Compile-only ABI template used to compare the generated intermediary tests with Clang's x86-64 vectorcall implementation. 
// It intentionally has no runtime or standard-library dependencies, so it can be cross-compiled for both Windows and Linux.

typedef float float4 __attribute__((vector_size(16)));

typedef struct {
    float x;
    float y;
} float2;

typedef struct {
    float x;
    float y;
    float z;
} float3;

typedef struct {
    float x;
    float y;
    float z;
    float w;
} float4_hva;

typedef struct {
    float4 first;
    float4 second;
} vector_pair;

typedef struct {
    float4 value;
} vector_single;

#define NOINLINE __attribute__((noinline))
#define VECTORCALL __attribute__((vectorcall))
#define ABI_NAME(name) __asm__(#name "_vc")
#define PROBE_PADDING() __asm__ volatile( \
    "nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t" \
    "nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\t" \
    "nop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop\n\tnop")

NOINLINE VECTORCALL float hva_discontiguous(float, float2, float) ABI_NAME(hva_discontiguous);
NOINLINE VECTORCALL float hva_exhausted(float, float, float, float, float3) ABI_NAME(hva_exhausted);
NOINLINE VECTORCALL float hva_vector_members(vector_pair) ABI_NAME(hva_vector_members);
NOINLINE VECTORCALL float hva_single_vector_member(vector_single) ABI_NAME(hva_single_vector_member);
NOINLINE VECTORCALL float hva_with_register_gaps(float, long, float, float2, float)
    ABI_NAME(hva_with_register_gaps);
NOINLINE VECTORCALL float hva_overlaps_later_scalar(float, float, float, float, float3, float)
    ABI_NAME(hva_overlaps_later_scalar);
NOINLINE VECTORCALL float seven_scalars(float, float, float, float, float, float, float)
    ABI_NAME(seven_scalars);
NOINLINE VECTORCALL float hva_partial_register_space(float, float, float, float, float, float3)
    ABI_NAME(hva_partial_register_space);
NOINLINE VECTORCALL vector_pair hva_vector_return(void) ABI_NAME(hva_vector_return);
NOINLINE VECTORCALL float4_hva hva_return(void) ABI_NAME(hva_return);
NOINLINE VECTORCALL long vectorcall_preservation_probe(long) ABI_NAME(vectorcall_preservation_probe);
typedef vector_pair (VECTORCALL *hva_vector_return_function)(void);
typedef float4_hva (VECTORCALL *hva_return_function)(void);
static hva_vector_return_function volatile hva_vector_return_target = &hva_vector_return;
static hva_return_function volatile hva_return_target = &hva_return;

NOINLINE VECTORCALL float hva_discontiguous(float first, float2 pair, float last)
{
    PROBE_PADDING();
    return first + pair.x + pair.y + last;
}

NOINLINE VECTORCALL float hva_exhausted(
    float first,
    float second,
    float third,
    float fourth,
    float3 aggregate)
{
    PROBE_PADDING();
    return first + second + third + fourth + aggregate.x + aggregate.y + aggregate.z;
}

NOINLINE VECTORCALL float hva_vector_members(vector_pair aggregate)
{
    PROBE_PADDING();
    return aggregate.first[0] + aggregate.second[0];
}

NOINLINE VECTORCALL float hva_single_vector_member(vector_single aggregate)
{
    PROBE_PADDING();
    return aggregate.value[0] + aggregate.value[2];
}

NOINLINE VECTORCALL float hva_with_register_gaps(
    float first,
    long integer,
    float third,
    float2 aggregate,
    float fifth)
{
    PROBE_PADDING();
    return first + (float)integer + third + aggregate.x + aggregate.y + fifth;
}

NOINLINE VECTORCALL float hva_overlaps_later_scalar(
    float first,
    float second,
    float third,
    float fourth,
    float3 aggregate,
    float sixth)
{
    PROBE_PADDING();
    return first + second + third + fourth + aggregate.x + aggregate.y + aggregate.z + sixth;
}

NOINLINE VECTORCALL float seven_scalars(
    float first,
    float second,
    float third,
    float fourth,
    float fifth,
    float sixth,
    float seventh)
{
    PROBE_PADDING();
    return first + second + third + fourth + fifth + sixth + seventh;
}

NOINLINE VECTORCALL float hva_partial_register_space(
    float first,
    float second,
    float third,
    float fourth,
    float fifth,
    float3 aggregate)
{
    PROBE_PADDING();
    return first + second + third + fourth + fifth + aggregate.x + aggregate.y + aggregate.z;
}

NOINLINE VECTORCALL vector_pair hva_vector_return(void)
{
    PROBE_PADDING();
    vector_pair result = { { 1, 2, 3, 4 }, { 5, 6, 7, 8 } };
    return result;
}

NOINLINE VECTORCALL float4_hva hva_return(void)
{
    PROBE_PADDING();
    float4_hva result = { 1, 2, 3, 4 };
    return result;
}

NOINLINE VECTORCALL long vectorcall_preservation_probe(long value)
{
    __asm__ volatile("" : : : "rdi", "rsi", "xmm6", "xmm7");
    return value;
}

NOINLINE float call_hva_discontiguous(float first, float2 pair, float last)
{
    return hva_discontiguous(first, pair, last);
}

NOINLINE float call_hva_exhausted(
    float first,
    float second,
    float third,
    float fourth,
    float3 aggregate)
{
    return hva_exhausted(first, second, third, fourth, aggregate);
}

NOINLINE float call_hva_vector_members(vector_pair aggregate)
{
    return hva_vector_members(aggregate);
}

NOINLINE float call_hva_with_register_gaps(
    float first,
    long integer,
    float third,
    float2 aggregate,
    float fifth)
{
    return hva_with_register_gaps(first, integer, third, aggregate, fifth);
}

NOINLINE float call_hva_overlaps_later_scalar(
    float first,
    float second,
    float third,
    float fourth,
    float3 aggregate,
    float sixth)
{
    return hva_overlaps_later_scalar(first, second, third, fourth, aggregate, sixth);
}

NOINLINE float call_seven_scalars(
    float first,
    float second,
    float third,
    float fourth,
    float fifth,
    float sixth,
    float seventh)
{
    return seven_scalars(first, second, third, fourth, fifth, sixth, seventh);
}

NOINLINE float call_hva_partial_register_space(
    float first,
    float second,
    float third,
    float fourth,
    float fifth,
    float3 aggregate)
{
    return hva_partial_register_space(first, second, third, fourth, fifth, aggregate);
}

NOINLINE vector_pair call_hva_vector_return(void)
{
    return hva_vector_return_target();
}

NOINLINE void call_hva_vector_return_out(float *output)
{
    vector_pair result = hva_vector_return_target();
    output[0] = result.first[0];
    output[1] = result.first[1];
    output[2] = result.first[2];
    output[3] = result.first[3];
    output[4] = result.second[0];
    output[5] = result.second[1];
    output[6] = result.second[2];
    output[7] = result.second[3];
}

NOINLINE float4_hva call_hva_return(void)
{
    return hva_return_target();
}

NOINLINE void *address_hva_discontiguous(void)
{
    return (void *)&hva_discontiguous;
}

NOINLINE void *address_hva_exhausted(void)
{
    return (void *)&hva_exhausted;
}

NOINLINE void *address_hva_vector_members(void)
{
    return (void *)&hva_vector_members;
}

NOINLINE void *address_hva_return(void)
{
    return (void *)&hva_return;
}

NOINLINE void *address_hva_partial_register_space(void)
{
    return (void *)&hva_partial_register_space;
}

NOINLINE void *address_hva_vector_return(void)
{
    return (void *)&hva_vector_return;
}
