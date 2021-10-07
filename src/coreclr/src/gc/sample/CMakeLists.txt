project(clrgcsample)

include(${CLR_ENG_NATIVE_DIR}/configurecompiler.cmake)

set(CMAKE_INCLUDE_CURRENT_DIR ON)

include_directories(..)
include_directories(../env)

set(SOURCES
    GCSample.cpp
    gcenv.ee.cpp
    ../gceventstatus.cpp
    ../gcconfig.cpp
    ../gccommon.cpp
    ../gceewks.cpp
    ../gchandletable.cpp
    ../gcscan.cpp
    ../gcwks.cpp
    ../gcload.cpp
    ../handletable.cpp
    ../handletablecache.cpp
    ../handletablecore.cpp
    ../handletablescan.cpp
    ../objecthandle.cpp
    ../softwarewritewatch.cpp
)

if (CLR_CMAKE_TARGET_ARCH_AMD64 AND CLR_CMAKE_TARGET_WIN32)
  set ( SOURCES
    ${SOURCES}
    ../vxsort/isa_detection_dummy.cpp
    ../vxsort/do_vxsort_avx2.cpp
    ../vxsort/do_vxsort_avx512.cpp
    ../vxsort/machine_traits.avx2.cpp
    ../vxsort/smallsort/bitonic_sort.AVX2.int64_t.generated.cpp
    ../vxsort/smallsort/bitonic_sort.AVX2.int32_t.generated.cpp
    ../vxsort/smallsort/bitonic_sort.AVX512.int64_t.generated.cpp
    ../vxsort/smallsort/bitonic_sort.AVX512.int32_t.generated.cpp
    ../vxsort/smallsort/avx2_load_mask_tables.cpp
)
endif (CLR_CMAKE_TARGET_ARCH_AMD64 AND CLR_CMAKE_TARGET_WIN32)

if(CLR_CMAKE_TARGET_WIN32)
  set (GC_LINK_LIBRARIES
    utilcodestaticnohost
    ${STATIC_MD_CRT_LIB}
    kernel32.lib
    advapi32.lib
    Psapi.lib
    oleaut32.lib
    user32.lib
    ntdll.lib
    )
endif(CLR_CMAKE_TARGET_WIN32)

if(CLR_CMAKE_TARGET_WIN32)
    list(APPEND SOURCES
        ../windows/gcenv.windows.cpp)
    add_definitions(-DUNICODE)
    add_definitions(-DWIN32)
    add_definitions(-D_WIN32)
    add_definitions(-DWINVER=0x0501)
    add_definitions(-D_WIN32_WINNT=0x0501)
    add_definitions(-DWIN32_LEAN_AND_MEAN)
    add_definitions(-D_CRT_SECURE_NO_WARNINGS)
else()
    list(APPEND SOURCES
        ../gcenv.unix.cpp)
endif()

_add_executable(gcsample
    ${SOURCES}
)

if(CLR_CMAKE_TARGET_WIN32)
    target_link_libraries(gcsample ${GC_LINK_LIBRARIES})
endif()
