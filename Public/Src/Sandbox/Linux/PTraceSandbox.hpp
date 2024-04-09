// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include "bxl_observer.hpp"

typedef void (*HandlerFunction)(void);

#define MAKE_HANDLER_FN_NAME(syscallName) Handle##syscallName
#define MAKE_HANDLER_FN_DEF(syscallName) void MAKE_HANDLER_FN_NAME(syscallName) ()
#define MAKE_HANDLER_FN_DEF_NEW(syscallName) MAKE_HANDLER_FN_DEF(new##syscallName)

/*
 * See the documentation section of the repository for an explanation on how this all works along with some helpful resources.
 * 
 * A note on error reporting for the ptraced operations: the interposing sandbox reports errnos for all failed operations. This
 * is noticeably more expensive to do for the ptrace-based sandbox. So in order to not hurt performance, we only cherry-pick some
 * particular functions where we want to report back their return values (the ones whose return values are actually used on bxl managed side
 * as of today). The other detail is that the interposing sandbox reports the errno, whereas this sandbox reports the return value. The reason
 * is that peeking into errno is not easy for ptrace (or we haven't figured out how to do that yet). This means that on bxl side we should rely
 * on checking for zero error code to mean success, but we shouldn't look for particular errnos. 
 * TODO: if we can't finally figure out how to peek at errno, consider changing the currently reported error in the access report to a boolean-looking 
 * value across the board (ideally including Windows as well) so we have a consistent way of looking at this that looks the same for all sandboxes.
 */

class PTraceSandbox
{
public:
    PTraceSandbox(BxlObserver *bxl);
    ~PTraceSandbox();
    
    /**
     * Attach the tracer to the provided pid.
     */
    void AttachToProcess(pid_t traceePid, std::string exe, std::string semaphoreName);

    /*
     * @brief Executes the provided child process under the ptrace sandbox
     * @return The return value from exec if the child fails to execute
     */
    int ExecuteWithPTraceSandbox(const char *file, char *const argv[], char *const envp[], const char *fam);

private:
    BxlObserver *m_bxl;
    pid_t m_traceePid = 0;
    std::vector<std::tuple<pid_t, std::string>> m_traceeTable; // tracee pid, tracee exe path

    /**
     * Removes the current pid from the tracee table and reports its exit
     */
    void RemoveFromTraceeTable();

    /**
     * Finds the parent process in the tracee table for a given PID.
     */
    std::vector<std::tuple<pid_t, std::string>>::iterator FindProcess(pid_t pid);

    void HandleSysCallGeneric(int syscallNumber);

    void *GetArgumentAddr(int index);

    /*
     * Given a set of registers from PTRACE_GETREGS, this function will return the value of the argument at the given index.
    */
    static unsigned long long ArgumentIndexToRegister(int index, struct user_regs_struct *regs);

    // @brief Gets the offset to read an argument at a given index starting from 1 (0 is used for the return value of the function)
    std::string ReadArgumentString(char *syscall, int argumentIndex, bool nullTerminated, int length = 0);

    /*
     * Gets a string at the provided address.
     */
    std::string ReadArgumentStringAtAddr(char *syscall, char *addr, bool nullTerminated, int length);
    /*
     * @brief Reads an argument string at a given address with ptrace
     * @param argumentIndex Index of the argument to read starting from 1 (or 0 for the return value)
     * @param nullTerminated Set this if the argument is null terminated
     * @param length Set this to the length of the argument if the argument is not null terminated
     * @return String containing the argument
     */
    unsigned long ReadArgumentLong(int argumentIndex);

    /**
     * Reads a set of arguments provided to an execve or an execveat call.
     */
    std::string ReadArgumentVector(char *syscall, int argumentIndex);

    void ReportOpen(std::string path, int oflag, std::string syscallName);
    void ReportCreate(std::string syscallName, int dirfd, const char *pathname, mode_t mode, long returnValue = 0, bool checkCache = true);
    int GetErrno();
    void UpdateTraceeTableForExec(std::string exePath);

    // Handlers
    MAKE_HANDLER_FN_DEF(execveat);
    MAKE_HANDLER_FN_DEF(execve);
    MAKE_HANDLER_FN_DEF(stat);
    MAKE_HANDLER_FN_DEF(lstat);
    MAKE_HANDLER_FN_DEF(fstat);
    MAKE_HANDLER_FN_DEF_NEW(fstatat);
    MAKE_HANDLER_FN_DEF(access);
    MAKE_HANDLER_FN_DEF(faccessat);
    MAKE_HANDLER_FN_DEF(creat);
    MAKE_HANDLER_FN_DEF(open);
    MAKE_HANDLER_FN_DEF(openat);
    MAKE_HANDLER_FN_DEF(write);
    MAKE_HANDLER_FN_DEF(writev);
    MAKE_HANDLER_FN_DEF(pwritev);
    MAKE_HANDLER_FN_DEF(pwritev2);
    MAKE_HANDLER_FN_DEF(pwrite64);
    MAKE_HANDLER_FN_DEF(truncate);
    MAKE_HANDLER_FN_DEF(ftruncate);
    MAKE_HANDLER_FN_DEF(rmdir);
    MAKE_HANDLER_FN_DEF(rename);
    MAKE_HANDLER_FN_DEF(renameat);
    MAKE_HANDLER_FN_DEF(renameat2);
    MAKE_HANDLER_FN_DEF(link);
    MAKE_HANDLER_FN_DEF(linkat);
    MAKE_HANDLER_FN_DEF(unlink);
    MAKE_HANDLER_FN_DEF(unlinkat);
    MAKE_HANDLER_FN_DEF(symlink);
    MAKE_HANDLER_FN_DEF(symlinkat);
    MAKE_HANDLER_FN_DEF(readlink);
    MAKE_HANDLER_FN_DEF(readlinkat);
    MAKE_HANDLER_FN_DEF(utime);
    MAKE_HANDLER_FN_DEF(utimes);
    MAKE_HANDLER_FN_DEF(utimensat);
    MAKE_HANDLER_FN_DEF(futimesat);
    MAKE_HANDLER_FN_DEF(mkdir);
    MAKE_HANDLER_FN_DEF(mkdirat);
    MAKE_HANDLER_FN_DEF(mknod);
    MAKE_HANDLER_FN_DEF(mknodat);
    MAKE_HANDLER_FN_DEF(chmod);
    MAKE_HANDLER_FN_DEF(fchmod);
    MAKE_HANDLER_FN_DEF(fchmodat);
    MAKE_HANDLER_FN_DEF(chown);
    MAKE_HANDLER_FN_DEF(fchown);
    MAKE_HANDLER_FN_DEF(lchown);
    MAKE_HANDLER_FN_DEF(fchownat);
    MAKE_HANDLER_FN_DEF(sendfile);
    MAKE_HANDLER_FN_DEF(copy_file_range);
    MAKE_HANDLER_FN_DEF(name_to_handle_at);
    MAKE_HANDLER_FN_DEF(exit);
    MAKE_HANDLER_FN_DEF(fork);
    MAKE_HANDLER_FN_DEF(clone);
    void HandleChildProcess(const char *syscall);
    void HandleRenameGeneric(const char *syscall, int olddirfd, const char *oldpath, int newdirfd, const char *newpath);
    void HandleReportAccessFd(const char *syscall, int fd, es_event_type_t eventType = ES_EVENT_TYPE_NOTIFY_WRITE);
};
