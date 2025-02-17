// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <assert.h>
#include <unistd.h>
#include <time.h>
#include <errno.h>

#include "pal_config.h"
#include "pal_random.h"

/*

Generate random bytes. The generated bytes are not cryptographically strong.

*/

void SystemNative_GetNonCryptographicallySecureRandomBytes(uint8_t* buffer, int32_t bufferLength)
{
    assert(buffer != NULL);

#if HAVE_ARC4RANDOM_BUF
    arc4random_buf(buffer, (size_t)bufferLength);
#else
    long num = 0;
    static bool sInitializedMRand;

    // Fall back to the secure version
    SystemNative_GetCryptographicallySecureRandomBytes(buffer, bufferLength);

    if (!sInitializedMRand)
    {
        srand48((long int)time(NULL));
        sInitializedMRand = true;
    }

    // always xor srand48 over the whole buffer to get some randomness
    // in case /dev/urandom is not really random

    for (int i = 0; i < bufferLength; i++)
    {
        if (i % 4 == 0)
        {
            num = lrand48();
        }

        *(buffer + i) ^= num;
        num >>= 8;
    }
#endif // HAVE_ARC4RANDOM_BUF
}

/*

Generate cryptographically strong random bytes.

Return 0 on success, -1 on failure.
*/
int32_t SystemNative_GetCryptographicallySecureRandomBytes(uint8_t* buffer, int32_t bufferLength)
{
    static volatile int rand_des = -1;
    static bool sMissingDevURandom;

    assert(buffer != NULL);

    if (!sMissingDevURandom)
    {
        if (rand_des == -1)
        {
            int fd;

            do
            {
#if HAVE_O_CLOEXEC
                fd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
#else
                fd = open("/dev/urandom", O_RDONLY);
                fcntl(fd, F_SETFD, FD_CLOEXEC);
#endif
            }
            while ((fd == -1) && (errno == EINTR));

            if (fd != -1)
            {
                int expected = -1;
                if (!__atomic_compare_exchange_n(&rand_des, &expected, fd, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST))
                {
                    // Another thread has already set the rand_des
                    close(fd);
                }
            }
            else if (errno == ENOENT)
            {
                sMissingDevURandom = true;
            }
        }

        if (rand_des != -1)
        {
            int32_t offset = 0;
            do
            {
                ssize_t n = read(rand_des, buffer + offset , (size_t)(bufferLength - offset));
                if (n == -1)
                {
                    if (errno == EINTR)
                    {
                        continue;
                    }
                    return -1;
                }

                offset += n;
            }
            while (offset != bufferLength);
            return 0;
        }
    }

    return -1;
}
