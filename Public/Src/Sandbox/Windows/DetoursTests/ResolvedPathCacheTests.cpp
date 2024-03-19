// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "stdafx.h"

#include <windows.h>
#include <tchar.h>
#include <stdio.h>
#include <strsafe.h>
#include <cstdio>

#if _MSC_VER >= 1200
#pragma warning(disable:4464) // Disable: relative include path contains '..'
#endif


#include "Logging.h"
#include "Utils.h"

#include "ResolvedPathCacheTests.h"

// warning C26485: Expression 'buffer': No array to pointer decay (bounds.3).
// warning C26472: Don't use a static_cast for arithmetic conversions. Use brace initialization, gsl::narrow_cast or gsl::narrow (type.1).
#pragma warning( disable : 26485 26472 )

// Used to test the in process ResolvedPathCache 
// Path casing is intentionally changed throughout the test to make sure the cache deals with casing properly
int CallDetoursResolvedPathCacheTests()
{
    std::string content = "Some text";

    // Create a file through a symlink
    HANDLE hFile = CreateFileW(
        L"First_DirectorySymlink\\output.txt",
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    DWORD bytes_written;
    if (!WriteFile(hFile, content.c_str(), static_cast<DWORD>(content.size()), &bytes_written, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Read the created file through a symlink
    hFile = CreateFileW(
        L"First_DirectorySymlink\\OUTPUT.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    DWORD bytes_read = 0;
    char buffer[1024]{};

    if (!ReadFile(hFile, buffer, 1024, &bytes_read, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Invalidate the resolved path cache
    if (!RemoveDirectoryW(L"SECOND_DirectorySymlink"))
    {
        return static_cast<int>(GetLastError());
    }

    // Recreate the symbolic link chain
    if (!TestCreateSymbolicLinkW(L"Second_DirectorySymlink", L"SourceDirectory", SYMBOLIC_LINK_FLAG_DIRECTORY))
    {
        return static_cast<int>(GetLastError());
    }

    // Read the created file through a symlink again
    hFile = CreateFileW(
        L"FIRST_DirectorySymlink\\output.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    if (!ReadFile(hFile, buffer, 1024, &bytes_read, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);
    return 0;
}

// Tests the resolved path cache works as expected when the same path has to be resolved with and without preserving
// its last reparse point segment
int CallDetoursResolvedPathPreservingLastSegmentCacheTests()
{
    // GetFileAttributes preserves the last reparse point
    GetFileAttributes(L"Directory\\FileSymlink");

    // Read the symlink. This operation does not preserve the last reparse point
    HANDLE hFile = CreateFileW(
        L"Directory\\FileSymlink",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Repeat the steps above, so we exercize the cache
    GetFileAttributes(L"Directory\\FileSymlink");
    hFile = CreateFileW(
        L"Directory\\FileSymlink",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    return 0;
}

int CallDetoursResolvedPathCacheDealsWithUnicode()
{
    std::string content = "Some text";

    // Create a file through a symlink
    HANDLE hFile = CreateFileW(
        L"First_DirectorySymlink�\\output�.txt",
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    DWORD bytes_written;
    if (!WriteFile(hFile, content.c_str(), static_cast<DWORD>(content.size()), &bytes_written, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Read the created file through a symlink
    hFile = CreateFileW(
        L"FIRST_DirectorySymlink�\\OUTPUT�.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    DWORD bytes_read = 0;
    char buffer[1024]{};

    if (!ReadFile(hFile, buffer, 1024, &bytes_read, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Invalidate the resolved path cache
    if (!RemoveDirectoryW(L"FIRST_DirectorySymlink�"))
    {
        return static_cast<int>(GetLastError());
    }

    // Recreate the symbolic link chain
    if (!TestCreateSymbolicLinkW(L"First_DirectorySymlink�", L"SourceDirectory�", SYMBOLIC_LINK_FLAG_DIRECTORY))
    {
        return static_cast<int>(GetLastError());
    }

    // Read the created file through a symlink again
    hFile = CreateFileW(
        L"FIRST_DirectorySymlink�\\output�.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    if (!ReadFile(hFile, buffer, 1024, &bytes_read, nullptr))
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);
    return 0;
}

int CallDeleteDirectorySymlinkThroughDifferentPath()
{
    // Create a file through a symlink
    HANDLE hFile = CreateFileW(
        L"D1.lnk\\E.lnk\\f.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);

    // Invalidate the resolved path cache
    if (!RemoveDirectoryW(L"D2.lnk\\E.lnk"))
    {
        return static_cast<int>(GetLastError());
    }

    // Recreate the symbolic link chain
    if (!TestCreateSymbolicLinkW(L"D\\E.lnk", L"X", SYMBOLIC_LINK_FLAG_DIRECTORY))
    {
        return static_cast<int>(GetLastError());
    }

    // Read the created file through a symlink again
    hFile = CreateFileW(
        L"D1.lnk\\E.lnk\\f.txt",
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);

    if (hFile == INVALID_HANDLE_VALUE)
    {
        return static_cast<int>(GetLastError());
    }

    CloseHandle(hFile);
    return 0;
}
