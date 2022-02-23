/*
    Copyright (c) 2005-2021 Intel Corporation

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

#include "oneapi/tbb/version.h"

#include "oneapi/tbb/detail/_exception.h"
#include "oneapi/tbb/detail/_assert.h"
#include "oneapi/tbb/detail/_utils.h"
#include "oneapi/tbb/tbb_allocator.h" // Is this OK?
#include "oneapi/tbb/cache_aligned_allocator.h"

#include "dynamic_link.h"
#include "misc.h"

#include <cstdlib>

#if _WIN32 || _WIN64
#include <Windows.h>
#else
#include <dlfcn.h>
#endif /* _WIN32||_WIN64 */

#if defined(_WIN32)
#define IMPORT
#else
#define IMPORT
#endif

extern "C" {
    IMPORT void* scalable_malloc(std::size_t);
    IMPORT void  scalable_free(void*);
    IMPORT void* scalable_aligned_malloc(std::size_t, std::size_t);
    IMPORT void  scalable_aligned_free(void*);
}

namespace tbb {
namespace detail {
namespace r1 {

// TODO: use CPUID to find actual line size, though consider backward compatibility
// nfs - no false sharing
static constexpr std::size_t nfs_size = 128;

std::size_t __TBB_EXPORTED_FUNC cache_line_size() {
    return nfs_size;
}

void* __TBB_EXPORTED_FUNC cache_aligned_allocate(std::size_t size) {
    const std::size_t cache_line_size = nfs_size;
    __TBB_ASSERT(is_power_of_two(cache_line_size), "must be power of two");

    // Check for overflow
    if (size + cache_line_size < size) {
        throw_exception(exception_id::bad_alloc);
    }
    // scalable_aligned_malloc considers zero size request an error, and returns NULL
    if (size == 0) size = 1;

    void* result = scalable_aligned_malloc(size, cache_line_size);
    if (!result) {
        throw_exception(exception_id::bad_alloc);
    }
    __TBB_ASSERT(is_aligned(result, cache_line_size), "The returned address isn't aligned");
    return result;
}

void __TBB_EXPORTED_FUNC cache_aligned_deallocate(void* p) {
    scalable_aligned_free(p);
}

void* __TBB_EXPORTED_FUNC allocate_memory(std::size_t size) {
    void* result = scalable_malloc(size);
    if (!result) {
        throw_exception(exception_id::bad_alloc);
    }
    return result;
}

void __TBB_EXPORTED_FUNC deallocate_memory(void* p) {
    if (p) {
        scalable_free(p);
    }
}

bool __TBB_EXPORTED_FUNC is_tbbmalloc_used() {
    return true;
}

} // namespace r1
} // namespace detail
} // namespace tbb
