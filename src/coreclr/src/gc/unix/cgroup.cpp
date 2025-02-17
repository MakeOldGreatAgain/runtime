// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*++

Module Name:

    cgroup.cpp

Abstract:
    Read memory and cpu limits for the current process
--*/
#ifdef __FreeBSD__
#define _WITH_GETLINE
#endif

#include <cstdint>
#include <cstddef>
#include <cassert>
#include <unistd.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <sys/resource.h>
#if defined(__APPLE__) || defined(__FreeBSD__)
#include <sys/param.h>
#include <sys/mount.h>
#else
#include <sys/vfs.h>
#endif
#include <errno.h>
#include <limits>

#include "cgroup.h"

#ifndef SIZE_T_MAX
#define SIZE_T_MAX (~(size_t)0)
#endif

#define CGROUP2_SUPER_MAGIC 0x63677270
#define TMPFS_MAGIC 0x01021994

#define BASE_TEN 10

#define PROC_MOUNTINFO_FILENAME "/proc/self/mountinfo"
#define PROC_CGROUP_FILENAME "/proc/self/cgroup"
#define PROC_STATM_FILENAME "/proc/self/statm"
#define CGROUP1_MEMORY_LIMIT_FILENAME "/memory.limit_in_bytes"
#define CGROUP2_MEMORY_LIMIT_FILENAME "/memory.max"
#define CGROUP_MEMORY_STAT_FILENAME "/memory.stat"
#define CGROUP1_CFS_QUOTA_FILENAME "/cpu.cfs_quota_us"
#define CGROUP1_CFS_PERIOD_FILENAME "/cpu.cfs_period_us"
#define CGROUP2_CPU_MAX_FILENAME "/cpu.max"

extern bool ReadMemoryValueFromFile(const char* filename, uint64_t* val);

class CGroup
{
    // the cgroup version number or 0 to indicate cgroups are not found or not enabled
    static int s_cgroup_version;

    static char *s_memory_cgroup_path;
    static char *s_cpu_cgroup_path;

    static const char *s_mem_stat_key_names[];
    static size_t s_mem_stat_key_lengths[];
    static size_t s_mem_stat_n_keys;
public:
    static void Initialize()
    {
        s_cgroup_version = FindCGroupVersion();
        s_memory_cgroup_path = FindCGroupPath(s_cgroup_version == 1 ? &IsCGroup1MemorySubsystem : nullptr);
        s_cpu_cgroup_path = FindCGroupPath(s_cgroup_version == 1 ? &IsCGroup1CpuSubsystem : nullptr);

        if (s_cgroup_version == 1)
        {
            s_mem_stat_n_keys = 4;
            s_mem_stat_key_names[0] = "total_inactive_anon ";
            s_mem_stat_key_names[1] = "total_active_anon ";
            s_mem_stat_key_names[2] = "total_dirty ";
            s_mem_stat_key_names[3] = "total_unevictable ";
        }
        else
        {
            s_mem_stat_n_keys = 3;
            s_mem_stat_key_names[0] = "anon ";
            s_mem_stat_key_names[1] = "file_dirty ";
            s_mem_stat_key_names[2] = "unevictable ";
        }

        for (size_t i = 0; i < s_mem_stat_n_keys; i++)
        {
            s_mem_stat_key_lengths[i] = strlen(s_mem_stat_key_names[i]);
        }
    }

    static void Cleanup()
    {
        free(s_memory_cgroup_path);
        free(s_cpu_cgroup_path);
    }

    static bool GetPhysicalMemoryLimit(uint64_t *val)
    {
        if (s_cgroup_version == 0)
            return false;
        else if (s_cgroup_version == 1)
            return GetCGroupMemoryLimit(val, CGROUP1_MEMORY_LIMIT_FILENAME);
        else if (s_cgroup_version == 2)
            return GetCGroupMemoryLimit(val, CGROUP2_MEMORY_LIMIT_FILENAME);
        else
        {
            assert(!"Unknown cgroup version.");
            return false;
        }
    }

    static bool GetPhysicalMemoryUsage(size_t *val)
    {
        if (s_cgroup_version == 0)
            return false;
        else if (s_cgroup_version == 1)
            return GetCGroupMemoryUsage(val);
        else if (s_cgroup_version == 2)
            return GetCGroupMemoryUsage(val);
        else
        {
            assert(!"Unknown cgroup version.");
            return false;
        }
    }

    static bool GetCpuLimit(uint32_t *val)
    {
        if (s_cgroup_version == 0)
            return false;
        else if (s_cgroup_version == 1)
            return GetCGroup1CpuLimit(val);
        else if (s_cgroup_version == 2)
            return GetCGroup2CpuLimit(val);
        else
        {
            assert(!"Unknown cgroup version.");
            return false;
        }
    }

private:
    static int FindCGroupVersion()
    {
        // It is possible to have both cgroup v1 and v2 enabled on a system.
        // Most non-bleeding-edge Linux distributions fall in this group. We
        // look at the file system type of /sys/fs/cgroup to determine which
        // one is the default. For more details, see:
        // https://systemd.io/CGROUP_DELEGATION/#three-different-tree-setups-
        // We dont care about the difference between the "legacy" and "hybrid"
        // modes because both of those involve cgroup v1 controllers managing
        // resources.

#if !HAVE_NON_LEGACY_STATFS
        return 0;
#else

        struct statfs stats;
        int result = statfs("/sys/fs/cgroup", &stats);
        if (result != 0)
            return 0;

        switch (stats.f_type)
        {
            case TMPFS_MAGIC: return 1;
            case CGROUP2_SUPER_MAGIC: return 2;
            default:
                assert(!"Unexpected file system type for /sys/fs/cgroup");
                return 0;
        }
#endif
    }

    static bool IsCGroup1MemorySubsystem(const char *strTok){
        return strcmp("memory", strTok) == 0;
    }

    static bool IsCGroup1CpuSubsystem(const char *strTok){
        return strcmp("cpu", strTok) == 0;
    }

    static char* FindCGroupPath(bool (*is_subsystem)(const char *)){
        char *cgroup_path = nullptr;
        char *hierarchy_mount = nullptr;
        char *hierarchy_root = nullptr;
        char *cgroup_path_relative_to_mount = nullptr;
        size_t common_path_prefix_len;

        FindHierarchyMount(is_subsystem, &hierarchy_mount, &hierarchy_root);
        if (hierarchy_mount == nullptr || hierarchy_root == nullptr)
            goto done;

        cgroup_path_relative_to_mount = FindCGroupPathForSubsystem(is_subsystem);
        if (cgroup_path_relative_to_mount == nullptr)
            goto done;

        cgroup_path = (char*)malloc(strlen(hierarchy_mount) + strlen(cgroup_path_relative_to_mount) + 1);
        if (cgroup_path == nullptr)
           goto done;

        strcpy(cgroup_path, hierarchy_mount);
        // For a host cgroup, we need to append the relative path.
        // The root and cgroup path can share a common prefix of the path that should not be appended.
        // Example 1 (docker):
        // hierarchy_mount:               /sys/fs/cgroup/cpu
        // hierarchy_root:                /docker/87ee2de57e51bc75175a4d2e81b71d162811b179d549d6601ed70b58cad83578
        // cgroup_path_relative_to_mount: /docker/87ee2de57e51bc75175a4d2e81b71d162811b179d549d6601ed70b58cad83578/my_named_cgroup
        // append do the cgroup_path:     /my_named_cgroup
        // final cgroup_path:             /sys/fs/cgroup/cpu/my_named_cgroup
        //
        // Example 2 (out of docker)
        // hierarchy_mount:               /sys/fs/cgroup/cpu
        // hierarchy_root:                /
        // cgroup_path_relative_to_mount: /my_named_cgroup
        // append do the cgroup_path:     /my_named_cgroup
        // final cgroup_path:             /sys/fs/cgroup/cpu/my_named_cgroup
        common_path_prefix_len = strlen(hierarchy_root);
        if ((common_path_prefix_len == 1) || strncmp(hierarchy_root, cgroup_path_relative_to_mount, common_path_prefix_len) != 0)
        {
            common_path_prefix_len = 0;
        }

        assert((cgroup_path_relative_to_mount[common_path_prefix_len] == '/') || (cgroup_path_relative_to_mount[common_path_prefix_len] == '\0'));

        strcat(cgroup_path, cgroup_path_relative_to_mount + common_path_prefix_len);


    done:
        free(hierarchy_mount);
        free(hierarchy_root);
        free(cgroup_path_relative_to_mount);
        return cgroup_path;
    }

    static void FindHierarchyMount(bool (*is_subsystem)(const char *), char** pmountpath, char** pmountroot)
    {
        char *line = nullptr;
        size_t lineLen = 0, maxLineLen = 0;
        char *filesystemType = nullptr;
        char *options = nullptr;
        char *mountpath = nullptr;
        char *mountroot = nullptr;

        FILE *mountinfofile = fopen(PROC_MOUNTINFO_FILENAME, "r");
        if (mountinfofile == nullptr)
            goto done;

        while (getline(&line, &lineLen, mountinfofile) != -1)
        {
            if (filesystemType == nullptr || lineLen > maxLineLen)
            {
                free(filesystemType);
                filesystemType = nullptr;
                free(options);
                options = nullptr;
                filesystemType = (char*)malloc(lineLen+1);
                if (filesystemType == nullptr)
                    goto done;
                options = (char*)malloc(lineLen+1);
                if (options == nullptr)
                    goto done;
                maxLineLen = lineLen;
            }

            char* separatorChar = strstr(line, " - ");

            // See man page of proc to get format for /proc/self/mountinfo file
            int sscanfRet = sscanf(separatorChar,
                                   " - %s %*s %s",
                                   filesystemType,
                                   options);
            if (sscanfRet != 2)
            {
                assert(!"Failed to parse mount info file contents with sscanf.");
                goto done;
            }

            if (strncmp(filesystemType, "cgroup", 6) == 0)
            {
                bool isSubsystemMatch = is_subsystem == nullptr;
                if (!isSubsystemMatch)
                {
                    char* context = nullptr;
                    char* strTok = strtok_r(options, ",", &context);
                    while (!isSubsystemMatch && strTok != nullptr)
                    {
                        isSubsystemMatch = is_subsystem(strTok);
                        strTok = strtok_r(nullptr, ",", &context);
                    }
                }
                if (isSubsystemMatch)
                {
                        mountpath = (char*)malloc(lineLen+1);
                        if (mountpath == nullptr)
                            goto done;
                        mountroot = (char*)malloc(lineLen+1);
                        if (mountroot == nullptr)
                            goto done;

                        sscanfRet = sscanf(line,
                                           "%*s %*s %*s %s %s ",
                                           mountroot,
                                           mountpath);
                        if (sscanfRet != 2)
                            assert(!"Failed to parse mount info file contents with sscanf.");

                        // assign the output arguments and clear the locals so we don't free them.
                        *pmountpath = mountpath;
                        *pmountroot = mountroot;
                        mountpath = mountroot = nullptr;
                }
            }
        }
    done:
        free(mountpath);
        free(mountroot);
        free(filesystemType);
        free(options);
        free(line);
        if (mountinfofile)
            fclose(mountinfofile);
    }

    static char* FindCGroupPathForSubsystem(bool (*is_subsystem)(const char *))
    {
        char *line = nullptr;
        size_t lineLen = 0;
        size_t maxLineLen = 0;
        char *subsystem_list = nullptr;
        char *cgroup_path = nullptr;
        bool result = false;

        FILE *cgroupfile = fopen(PROC_CGROUP_FILENAME, "r");
        if (cgroupfile == nullptr)
            goto done;

        while (!result && getline(&line, &lineLen, cgroupfile) != -1)
        {
            if (subsystem_list == nullptr || lineLen > maxLineLen)
            {
                free(subsystem_list);
                subsystem_list = nullptr;
                free(cgroup_path);
                cgroup_path = nullptr;
                subsystem_list = (char*)malloc(lineLen+1);
                if (subsystem_list == nullptr)
                    goto done;
                cgroup_path = (char*)malloc(lineLen+1);
                if (cgroup_path == nullptr)
                    goto done;
                maxLineLen = lineLen;
            }

            if (s_cgroup_version == 1)
            {
                // See man page of proc to get format for /proc/self/cgroup file
                int sscanfRet = sscanf(line,
                                       "%*[^:]:%[^:]:%s",
                                       subsystem_list,
                                       cgroup_path);
                if (sscanfRet != 2)
                {
                    assert(!"Failed to parse cgroup info file contents with sscanf.");
                    goto done;
                }

                char* context = nullptr;
                char* strTok = strtok_r(subsystem_list, ",", &context);
                while (strTok != nullptr)
                {
                    if (is_subsystem(strTok))
                    {
                        result = true;
                        break;
                    }
                    strTok = strtok_r(nullptr, ",", &context);
                }
            }
            else if (s_cgroup_version == 2)
            {
                // See https://www.kernel.org/doc/Documentation/cgroup-v2.txt
                // Look for a "0::/some/path"
                int sscanfRet = sscanf(line,
                                       "0::%s",
                                       cgroup_path);
                if (sscanfRet == 1)
                {
                    result = true;
                }
            }
            else
            {
                assert(!"Unknown cgroup version in mountinfo.");
                goto done;
            }
        }
    done:
        free(subsystem_list);
        if (!result)
        {
            free(cgroup_path);
            cgroup_path = nullptr;
        }
        free(line);
        if (cgroupfile)
            fclose(cgroupfile);
        return cgroup_path;
    }

    static bool GetCGroupMemoryLimit(uint64_t *val, const char *filename)
    {
        if (s_memory_cgroup_path == nullptr)
            return false;

        char* mem_limit_filename = nullptr;
        if (asprintf(&mem_limit_filename, "%s%s", s_memory_cgroup_path, filename) < 0)
            return false;

        bool result = ReadMemoryValueFromFile(mem_limit_filename, val);
        free(mem_limit_filename);
        return result;
    }

    static bool GetCGroupMemoryUsage(size_t *val)
    {
        if (s_memory_cgroup_path == nullptr)
            return false;

        char* stat_filename = nullptr;
        if (asprintf(&stat_filename, "%s%s", s_memory_cgroup_path, CGROUP_MEMORY_STAT_FILENAME) < 0)
            return false;

        FILE *stat_file = fopen(stat_filename, "r");
        free(stat_filename);
        if (stat_file == nullptr)
            return false;

        char *line = nullptr;
        size_t lineLen = 0;
        size_t readValues = 0;
        char* endptr;

        *val = 0;
        while (getline(&line, &lineLen, stat_file) != -1 && readValues < s_mem_stat_n_keys)
        {
            for (size_t i = 0; i < s_mem_stat_n_keys; i++)
            {
                if (strncmp(line, s_mem_stat_key_names[i], s_mem_stat_key_lengths[i]) == 0)
                {
                    errno = 0;
                    const char* startptr = line + s_mem_stat_key_lengths[i];
                    *val += strtoll(startptr, &endptr, 10);
                    if (endptr != startptr && errno == 0)
                        readValues++;

                    break;
                }
            }
        }

        fclose(stat_file);
        free(line);

        if (readValues == s_mem_stat_n_keys)
            return true;

        return false;
    }

    static bool GetCGroup1CpuLimit(uint32_t *val)
    {
        long long quota;
        long long period;

        quota = ReadCpuCGroupValue(CGROUP1_CFS_QUOTA_FILENAME);
        if (quota <= 0)
            return false;

        period = ReadCpuCGroupValue(CGROUP1_CFS_PERIOD_FILENAME);
        if (period <= 0)
            return false;

        ComputeCpuLimit(period, quota, val);

        return true;
    }

    static bool GetCGroup2CpuLimit(uint32_t *val)
    {
        char *filename = nullptr;
        FILE *file = nullptr;
        char *endptr = nullptr;
        char *max_quota_string = nullptr;
        char *period_string = nullptr;
        char *context = nullptr;
        char *line = nullptr;
        size_t lineLen = 0;

        long long quota = 0;
        long long period = 0;

        bool result = false;

        if (s_cpu_cgroup_path == nullptr)
            return false;

        if (asprintf(&filename, "%s%s", s_cpu_cgroup_path, CGROUP2_CPU_MAX_FILENAME) < 0)
            return false;

        file = fopen(filename, "r");
        if (file == nullptr)
            goto done;

        if (getline(&line, &lineLen, file) == -1)
            goto done;

        // The expected format is:
        //     $MAX $PERIOD
        // Where "$MAX" may be the string literal "max"

        max_quota_string = strtok_r(line, " ", &context);
        if (max_quota_string == nullptr)
        {
            assert(!"Unable to parse " CGROUP2_CPU_MAX_FILENAME " file contents.");
            goto done;
        }
        period_string = strtok_r(nullptr, " ", &context);
        if (period_string == nullptr)
        {
            assert(!"Unable to parse " CGROUP2_CPU_MAX_FILENAME " file contents.");
            goto done;
        }

        // "max" means no cpu limit
        if (strcmp("max", max_quota_string) == 0)
            goto done;

        errno = 0;
        quota = strtoll(max_quota_string, &endptr, BASE_TEN);
        if (max_quota_string == endptr || errno != 0)
            goto done;

        period = strtoll(period_string, &endptr, BASE_TEN);
        if (period_string == endptr || errno != 0)
            goto done;

        ComputeCpuLimit(period, quota, val);
        result = true;

    done:
        if (file)
            fclose(file);
        free(filename);
        free(line);

        return result;
    }

    static void ComputeCpuLimit(long long period, long long quota, uint32_t *val)
    {
        // Cannot have less than 1 CPU
        if (quota <= period)
        {
            *val = 1;
            return;
        }

        // Calculate cpu count based on quota and round it up
        double cpu_count = (double) quota / period  + 0.999999999;
        *val = (cpu_count < UINT32_MAX) ? (uint32_t)cpu_count : UINT32_MAX;
    }

    static long long ReadCpuCGroupValue(const char* subsystemFilename){
        char *filename = nullptr;
        bool result = false;
        long long val;

        if (s_cpu_cgroup_path == nullptr)
            return -1;

        if (asprintf(&filename, "%s%s", s_cpu_cgroup_path, subsystemFilename) < 0)
            return -1;

        result = ReadLongLongValueFromFile(filename, &val);
        free(filename);
        if (!result)
             return -1;

        return val;
    }

    static bool ReadLongLongValueFromFile(const char* filename, long long* val)
    {
        bool result = false;
        char *line = nullptr;
        size_t lineLen = 0;
        char *endptr = nullptr;

        if (val == nullptr)
            return false;

        FILE* file = fopen(filename, "r");
        if (file == nullptr)
            goto done;

        if (getline(&line, &lineLen, file) == -1)
            goto done;

        errno = 0;
        *val = strtoll(line, &endptr, BASE_TEN);
        if (line == endptr || errno != 0)
            goto done;

        result = true;
    done:
        if (file)
            fclose(file);
        free(line);
        return result;
    }
};

int CGroup::s_cgroup_version = 0;
char *CGroup::s_memory_cgroup_path = nullptr;
char *CGroup::s_cpu_cgroup_path = nullptr;

const char *CGroup::s_mem_stat_key_names[4] = {};
size_t CGroup::s_mem_stat_key_lengths[4] = {};
size_t CGroup::s_mem_stat_n_keys = 0;

void InitializeCGroup()
{
    CGroup::Initialize();
}

void CleanupCGroup()
{
    CGroup::Cleanup();
}

size_t GetRestrictedPhysicalMemoryLimit()
{
    uint64_t physical_memory_limit = 0;

    if (!CGroup::GetPhysicalMemoryLimit(&physical_memory_limit))
         return 0;

    // If there's no memory limit specified on the container this
    // actually returns 0x7FFFFFFFFFFFF000 (2^63-1 rounded down to
    // 4k which is a common page size). So we know we are not
    // running in a memory restricted environment.
    if (physical_memory_limit > 0x7FFFFFFF00000000)
    {
        return 0;
    }

    struct rlimit curr_rlimit;
    size_t rlimit_soft_limit = (size_t)RLIM_INFINITY;
    if (getrlimit(RLIMIT_AS, &curr_rlimit) == 0)
    {
        rlimit_soft_limit = curr_rlimit.rlim_cur;
    }
    physical_memory_limit = (physical_memory_limit < rlimit_soft_limit) ?
                            physical_memory_limit : rlimit_soft_limit;

    // Ensure that limit is not greater than real memory size
    long pages = sysconf(_SC_PHYS_PAGES);
    if (pages != -1)
    {
        long pageSize = sysconf(_SC_PAGE_SIZE);
        if (pageSize != -1)
        {
            physical_memory_limit = (physical_memory_limit < (size_t)pages * pageSize)?
                                    physical_memory_limit : (size_t)pages * pageSize;
        }
    }

    if (physical_memory_limit > std::numeric_limits<size_t>::max())
    {
        // It is observed in practice when the memory is unrestricted, Linux control
        // group returns a physical limit that is bigger than the address space
        return std::numeric_limits<size_t>::max();
    }
    else
    {
        return (size_t)physical_memory_limit;
    }
}

bool GetPhysicalMemoryUsed(size_t* val)
{
    bool result = false;
    size_t linelen;
    char* line = nullptr;

    if (val == nullptr)
        return false;

    // Linux uses cgroup usage to trigger oom kills.
    if (CGroup::GetPhysicalMemoryUsage(val))
        return true;

    // process resident set size.
    FILE* file = fopen(PROC_STATM_FILENAME, "r");
    if (file != nullptr && getline(&line, &linelen, file) != -1)
    {
        char* context = nullptr;
        char* strTok = strtok_r(line, " ", &context);
        strTok = strtok_r(nullptr, " ", &context);

        errno = 0;
        *val = strtoull(strTok, nullptr, 0);
        if (errno == 0)
        {
            long pageSize = sysconf(_SC_PAGE_SIZE);
            if (pageSize != -1)
            {
                *val = *val * pageSize;
                result = true;
            }
        }
    }

    if (file)
        fclose(file);
    free(line);
    return result;
}

bool GetCpuLimit(uint32_t* val)
{
    if (val == nullptr)
        return false;

    return CGroup::GetCpuLimit(val);
}
