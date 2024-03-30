// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <algorithm>
#include "PTraceSandbox.hpp"
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <sys/prctl.h>
#include <sys/ptrace.h>
#include <sys/reg.h>
#include <sys/wait.h>
#include <sys/user.h>

#define SYSCALL_NAME_TO_NUMBER(name) __NR_##name
#define SYSCALL_NAME_STRING(name) #name

// This macro fills in the seccomp_data data structure
// The first statement checks if the current syscall matches the syscall number provided.
// If so, then it will not modify the program counter (the 0 set in the third arg is for PC + 0)
// If the syscall number does not match, then it will increment the program counter (PC + 1) and jump to skip over the next statement that has the SECCOMP_RET_TRACE
// The BPF statement with SECCOMP_RET_TRACE indicates that we should invoke the tracer (ie: the parent process will be signalled by ptrace)
#define TRACE_SYSCALL(name) \
        BPF_JUMP(BPF_JMP+BPF_JEQ+BPF_K, SYSCALL_NAME_TO_NUMBER(name), 0, 1), \
        BPF_STMT(BPF_RET+BPF_K, SECCOMP_RET_TRACE)

// There are "new" versions of certain syscalls (such as fstatat).
// The name of the function does not include the "new" bit, but the name in the kernel includes this prefix
// Use this macro to trace those syscalls
// The other "new" variants of the macros in this file achieve the same thing
#define TRACE_SYSCALL_NEW(name) TRACE_SYSCALL(new##name)

#define HANDLER_FUNCTION(syscallName) void PTraceSandbox::MAKE_HANDLER_FN_NAME(syscallName) ()
#define HANDLER_FUNCTION_NEW(syscallName) HANDLER_FUNCTION(new##syscallName)

#define CHECK_AND_CALL_HANDLER(syscallName) \
        case SYSCALL_NAME_TO_NUMBER(syscallName): \
            PTraceSandbox::MAKE_HANDLER_FN_NAME(syscallName) (); \
            break
#define CHECK_AND_CALL_HANDLER_NEW(syscallName) CHECK_AND_CALL_HANDLER(new##syscallName)

PTraceSandbox::PTraceSandbox(BxlObserver *bxl)
{
    m_bxl = bxl;
}

PTraceSandbox::~PTraceSandbox()
{
}

int PTraceSandbox::ExecuteWithPTraceSandbox(const char *file, char *const argv[], char *const envp[], const char *fam)
{
    /**
     * NOTE: when adding new system calls to interpose here, ensure that a matching unit test for that system call
     * is added to Public/Src/Sandbox/Linux/UnitTests/TestProcesses/TestProcess/main.cpp and Public/Src/Engine/UnitTests/Processes/LinuxSandboxProcessTests.cs
     */

    // Filter for the syscalls that BXL is interested in tracing
    // Only the syscalls in here will be signalled to the main process by seccomp
    // List of available syscalls to ptrace: https://github.com/torvalds/linux/blob/master/arch/x86/entry/syscalls/syscall_64.tbl
    // NOTE: The set of syscalls here are not equivalent to the set of functions that are interposed by the regular sandbox
    // This is expected because not all of the interposed functions map directly to system calls in the kernel.
    // This set should capture all of the file accesses we already observe on the interpose sandbox.
    struct sock_filter filter[] = {
        // This statement loads the syscall number (seccomp_data.nr) into the accumulator
        BPF_STMT(BPF_LD+BPF_W+BPF_ABS, offsetof(struct seccomp_data, nr)),
        // The next set of statements indicates that we should stop the tracee if one of these syscalls are detected
        TRACE_SYSCALL(execveat),
        TRACE_SYSCALL(execve),
        TRACE_SYSCALL(stat),
        TRACE_SYSCALL(lstat),
        TRACE_SYSCALL(fstat),
        TRACE_SYSCALL_NEW(fstatat),
        TRACE_SYSCALL(access),
        TRACE_SYSCALL(faccessat),
        TRACE_SYSCALL(creat),
        TRACE_SYSCALL(open),
        TRACE_SYSCALL(openat),
        TRACE_SYSCALL(write),
        TRACE_SYSCALL(writev),
        TRACE_SYSCALL(pwritev),
        TRACE_SYSCALL(pwritev2),
        TRACE_SYSCALL(pwrite64),
        TRACE_SYSCALL(truncate),
        TRACE_SYSCALL(ftruncate),
        TRACE_SYSCALL(rmdir),
        TRACE_SYSCALL(rename),
        TRACE_SYSCALL(renameat),
        TRACE_SYSCALL(renameat2),
        TRACE_SYSCALL(link),
        TRACE_SYSCALL(linkat),
        TRACE_SYSCALL(unlink),
        TRACE_SYSCALL(unlinkat),
        TRACE_SYSCALL(symlink),
        TRACE_SYSCALL(symlinkat),
        TRACE_SYSCALL(readlink),
        TRACE_SYSCALL(readlinkat),
        TRACE_SYSCALL(utime),
        TRACE_SYSCALL(utimes),
        TRACE_SYSCALL(utimensat),
        TRACE_SYSCALL(futimesat),
        TRACE_SYSCALL(mkdir),
        TRACE_SYSCALL(mkdirat),
        TRACE_SYSCALL(mknod),
        TRACE_SYSCALL(mknodat),
        TRACE_SYSCALL(chmod),
        TRACE_SYSCALL(fchmod),
        TRACE_SYSCALL(fchmodat),
        TRACE_SYSCALL(chown),
        TRACE_SYSCALL(fchown),
        TRACE_SYSCALL(lchown),
        TRACE_SYSCALL(fchownat),
        TRACE_SYSCALL(sendfile),
        TRACE_SYSCALL(copy_file_range),
        TRACE_SYSCALL(name_to_handle_at),
        // NOTE: vfork is explicitly not traced here, see PTraceSandbox::UpdateTraceeTableForExec for more details
        TRACE_SYSCALL(fork),
        TRACE_SYSCALL(clone),
        // SECCOMP_RET_ALLOW tells seccomp to allow all of the calls that were being filtered above (as opposed to killing them)
        // This would happen if none of the syscall numbers above get matched, and therefore should not stop the tracee
        BPF_STMT(BPF_RET+BPF_K, SECCOMP_RET_ALLOW),
    };

    struct sock_fprog prog = {
        .len = (unsigned short) (sizeof(filter)/sizeof(filter[0])),
        .filter = filter,
    };

    // NOTE: sem_open must be called before we set the seccomp filter
    std::string semaphoreName = "/" + std::to_string(getpid());
    sem_t *semaphoreTracee = sem_open(semaphoreName.c_str(), O_CREAT, 0644, 0);
    if (semaphoreTracee == NULL || semaphoreTracee == SEM_FAILED)
    {
        BXL_LOG_DEBUG(m_bxl, "[PTrace] sem_open failed with: '%s'", strerror(errno));
        m_bxl->real__exit(-1);
    }

    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) == -1)
    {
        m_bxl->real_fprintf(stderr, "[BuildXL] clock_gettime failed: '%s'\n", strerror(errno));
        m_bxl->real__exit(-1);
    }
    ts.tv_sec += 15; // Waiting up to 15 seconds and then assuming something went wrong with the ptrace runner

    // Wait for the ptracerunner to post to this semaphore to indicate that it has attached successfully
    auto waitResult = sem_timedwait(semaphoreTracee, &ts);
    auto semWaitErrno = errno;

    // Regardless of whether we timed out or not, close/unlink the semaphore
    sem_close(semaphoreTracee);
    sem_unlink(semaphoreName.c_str());

    if (waitResult == -1)
    {
        // Tracer failed to attach within 15 seconds
        m_bxl->real_fprintf(stderr, "[PTrace] PTraceRunner failed to respond within 15 seconds with error: '%s'\n", strerror(semWaitErrno));
        m_bxl->real__exit(-1);
    }

    // This prctl call prevents the child process from having a higher privilege than its parent
    // It is necessary to make the next PR_SET_SECCOMP call work (or else the parent process would need to run as root)
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) == -1) {
        BXL_LOG_DEBUG(m_bxl, "prctl(PR_SET_NO_NEW_PRIVS) failed %d\n", 1);
        m_bxl->real_printf("prctl(PR_SET_NO_NEW_PRIVS) failed\n");
        m_bxl->real__exit(-1);
    }

    // Sets the seccomp filter
    // NOTE: Do not run anything other than execve after this statement
    if (prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &prog) == -1) {
        BXL_LOG_DEBUG(m_bxl, "PR_SET_SECCOMP with SECCOMP_MODE_FILTER failed %d\n", 1);
        m_bxl->real_printf("PR_SET_SECCOMP with SECCOMP_MODE_FILTER failed\n");
        m_bxl->real__exit(-1);
    }

    // Finally perform the exec syscall, this call to exec along with the syscalls from the child process should be filtered and reported to the tracer by seccomp
    return m_bxl->real_execvpe(file, argv, envp);
}

void PTraceSandbox::AttachToProcess(pid_t traceePid, std::string exe, std::string semaphoreName)
{
    BXL_LOG_DEBUG(m_bxl, "[PTrace] Starting tracer PID '%d' to trace PID '%d'", getpid(), traceePid);

    // PTRACE_O_TRACESYSGOOD: Sets bit 7 of the signal when delivering a system calls.
    // PTRACE_O_TRACESECCOMP: Enables ptrace events from seccomp on the child
    // PTRACE_O_TRACECLONE/FORK/VFORK: Ptrace will signal on clone/fork/vfork before the syscall returns back to the caller
    // PTRACE_O_TRACEEXIT: ptrace will signal before exit() returns back to the caller.
    unsigned long options = PTRACE_O_TRACESYSGOOD | PTRACE_O_TRACESECCOMP | PTRACE_O_TRACECLONE | PTRACE_O_TRACEFORK | PTRACE_O_TRACEVFORK | PTRACE_O_TRACEEXIT;

    int status;
    if (ptrace(PTRACE_SEIZE, traceePid, 0L, options) == -1)
    {
        BXL_LOG_DEBUG(m_bxl, "[PTrace] PTRACE_SEIZE failed with error: '%s'", strerror(errno));
        _exit(-1);
    }

    // Interrupt the child to verify that the process attached
    if (ptrace(PTRACE_INTERRUPT, traceePid, 0L, 0L) == -1)
    {
        BXL_LOG_DEBUG(m_bxl, "[PTrace] PTRACE_INTERRUPT failed with error: '%s'", strerror(errno));
        _exit(-1);
    }

    m_traceePid = traceePid;
    m_traceeTable.push_back(std::make_tuple(traceePid, exe));
    m_bxl->disable_fd_table();

    // Resume child
    ptrace(PTRACE_SYSCALL, m_traceePid, 0, 0);

    // Attach complete, signal the semaphore for the child to resume
    sem_t *semaphore = sem_open(semaphoreName.c_str(), O_CREAT, 0644, 0);
    if (semaphore == NULL)
    {
        BXL_LOG_DEBUG(m_bxl, "[PTrace] sem_open failed with: '%s'", strerror(errno));
        _exit(-1);
    }
    sem_post(semaphore); // Increment the semaphore to unblock the traced process
    sem_close(semaphore);

    // Main loop that handles signals from the child
    // wait should get signalled from the following:
    //  1. ptrace event (seccomp, clone, fork, vfork, exit)
    //  2. Child process exited with status code
    //  3. Child process exited with signal
    while (true)
    {
        // Passing -1 to waitpid has it wait for a signal from any PID
        // A call to wait is equivalent to waitpid(-1, &status, 0);
        // The wait call will return the PID of the process that signalled, this should be used as the traceepid
        // NOTE: this must be done in a single thread, we cannot split this up into separate threads because only the thread that attached the tracee can issue ptrace commands
        m_traceePid = wait(&status);

        if (m_traceePid == -1)
        {
            // ECHILD indicates that the calling process does not have any more children to wait on
            // If we don't get this, then we're in an abnormal state, this should be logged
            if (errno != ECHILD)
            {
                std::cerr << "[PTrace] wait returned -1 but did not set errno to ECHILD." << std::endl;
                _exit(-1);
            }

            _exit(0);
        }

        // Handle cases where the child processes has exited
        if (WIFEXITED(status) || WIFSIGNALED(status))
        {
            continue;
        }
        else if (!WIFSTOPPED(status))
        {
            std::cerr << "[PTrace] wait() returned bad status '" << status << "'" << std::endl;
            _exit(-1);
            break;
        }

        // Handle vfork/seccomp
        if (status >> 8 == (SIGTRAP | (PTRACE_EVENT_VFORK << 8)))
        {
            // This case is explicitly skipped, and handled by PTraceSandbox::UpdateTraceeTableForExec
            ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
        }
        else if (status >> 8 == (SIGTRAP | (PTRACE_EVENT_EXIT << 8)))
        {
            unsigned long traceeStatus = 0;
            ptrace(PTRACE_GETEVENTMSG, m_traceePid, NULL, &traceeStatus);
            BXL_LOG_DEBUG(m_bxl, "[PTrace] Tracee %d exited with exit code '%d'", m_traceePid, WEXITSTATUS(traceeStatus));
            RemoveFromTraceeTable();
            ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
        }
        else if (status >> 8 == (SIGTRAP | (PTRACE_EVENT_SECCOMP << 8)))
        {
            long syscallNumber = ptrace(PTRACE_PEEKUSER, m_traceePid, sizeof(long) * ORIG_RAX, NULL);
            HandleSysCallGeneric(syscallNumber);

            // We can resume the child with PTRACE_CONT here to ignore the ptrace-exit-stop for this syscall
            ptrace(PTRACE_CONT, m_traceePid, NULL, NULL);
        }
        else if (WIFSTOPPED(status) && !(WSTOPSIG(status) & 0x80))
        {
            // This is a signal-delivery-stop, this means that the tracee stopped during signal delivery
            // We don't care about these events, but when restarting the tracee we must deliver the signal by setting the last argument to ptrace(...)
            // signal-delivery-stop can be differentiated from sys calls events by checking whether the 7th bit is set on the signal (WSTOPSIG(status) & 0x80)
            ptrace(PTRACE_SYSCALL, m_traceePid, NULL, WSTOPSIG(status));
        }
        else
        {
            // We can ignore the ptrace-exit-stop for fork/vfork/clone/exit events here
            ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
        }
    }
}

void PTraceSandbox::RemoveFromTraceeTable()
{
    m_traceeTable.erase(std::remove_if
    (
        m_traceeTable.begin(),
        m_traceeTable.end(),
        [this](const std::tuple<pid_t, std::string>& item) { return std::get<0>(item) == m_traceePid; }
    ), m_traceeTable.end());

    Handleexit();
}

void *PTraceSandbox::GetArgumentAddr(int index)
{
    long addr = sizeof(long);

    // Order of first 6 arguments: %rdi, %rsi, %rdx, %rcx, %r8, and %r9
    switch (index) {
        case 0: // Return value
            addr *= RAX;
            break;
        case 1:
            addr *= RDI;
            break;
        case 2:
            addr *= RSI;
            break;
        case 3:
            addr *= RDX;
            break;
        case 4:
            addr *= R10;
            break;
        case 5:
            addr *= R8;
            break;
        case 6:
            addr *= R9;
            break;
        default:
            // Remaining arguments should be on the stack, but for what we need
            // the above 6 should be good enough and we should never hit this case
            addr = 0L;
            break;
    }

    return (void *)addr;
}

unsigned long long PTraceSandbox::ArgumentIndexToRegister(int index, struct user_regs_struct *regs)
{
    // Order of the arguments on the stack: %rdi, %rsi, %rdx, %rcx, %r8, and %r9
    // Reference: http://6.s081.scripts.mit.edu/sp18/x86-64-architecture-guide.html#:~:text=The%20caller%20uses%20registers%20to,off%20the%20stack%20in%20order.
    switch (index)
    {
        case 0: // Return value
            return regs->rax;
        case 1:
            return regs->rdi;
        case 2:
            return regs->rsi;
        case 3:
            return regs->rdx;
        case 4:
            return regs->r10;
        case 5:
            return regs->r8;
        case 6:
            return regs->r9;
        default:
            // We don't currently support reading more than the 6 arguments above with ptrace
            return 0;
    }
}

std::string PTraceSandbox::ReadArgumentString(char *syscall, int argumentIndex, bool nullTerminated, int length)
{
    void *addr = GetArgumentAddr(argumentIndex);
    char *addrRegValue = (char *)ptrace(PTRACE_PEEKUSER, m_traceePid, addr, 0);
    
    return ReadArgumentStringAtAddr(syscall, addrRegValue, nullTerminated, length);
}

std::string PTraceSandbox::ReadArgumentStringAtAddr(char *syscall, char *addr, bool nullTerminated, int length) {
    int currentStringLength = 0;
    std::string argument;

    argument.reserve(PATH_MAX); // We are mostly interested in reading paths from the arguments so PATH_MAX should be enough here for most cases

    while (true)
    {
        long addrMemoryLocation = ptrace(PTRACE_PEEKTEXT, m_traceePid, addr, NULL);
        if (addrMemoryLocation == -1)
        {
            BXL_LOG_DEBUG(m_bxl, "[PTrace] Error occured while executing PTRACE_PEEKTEXT for syscall '%s' : '%s'", syscall, strerror(errno));
            break;
        }

        addr += sizeof(long);

        char *currentArgReadChar = (char *)&addrMemoryLocation;
        bool finishedReadingArgument = false;

        for (int i = 0; i < sizeof(long); i++)
        {
            if ((nullTerminated && *currentArgReadChar == '\0') || (length > 0 && currentStringLength == length))
            {
                finishedReadingArgument = true;
                break;
            }

            argument.push_back(*currentArgReadChar);
            currentStringLength++;
            currentArgReadChar++;
        }

        if (finishedReadingArgument)
        {
            break;
        }
    }

    return argument;
}

unsigned long PTraceSandbox::ReadArgumentLong(int argumentIndex)
{
    void *addr = GetArgumentAddr(argumentIndex);
    return ptrace(PTRACE_PEEKUSER, m_traceePid, addr, NULL);
}

std::string PTraceSandbox::ReadArgumentVector(char *syscall, int argumentIndex)
{
    struct user_regs_struct regs;
    ptrace(PTRACE_GETREGS, m_traceePid, 0, &regs);

    auto addr = ArgumentIndexToRegister(argumentIndex, &regs); // Pointer to argv
    bool firstArgument = true;
    std::string arguments;
    arguments.reserve(PATH_MAX);

    while (true) {
        // Pointer to each individual element in the argv array
        auto argPtr = ptrace(PTRACE_PEEKTEXT, m_traceePid, addr, NULL);
        if (argPtr == -1) {
            BXL_LOG_DEBUG(m_bxl, "[PTrace] Error occured while parsing arguments for syscall '%s' with error %s", syscall, strerror(errno));
            break;
        }

        if (argPtr == 0) {
            // End of the argv array
            break;
        }

        arguments.append((!firstArgument ? " " : "") + ReadArgumentStringAtAddr(syscall, (char *)argPtr, /* nullTerminated */ true, /* length */ 0));
        addr += sizeof(unsigned long long);

        if (firstArgument) {
            firstArgument = false;
        }
    }

    return arguments;
}

int PTraceSandbox::GetErrno()
{
    long returnValue = ReadArgumentLong(0);
    return (returnValue == 0 ? 0 : 0xffffffffffffffffUL - returnValue);
}

// Handlers for each syscall
void PTraceSandbox::HandleSysCallGeneric(int syscallNumber)
{
    switch (syscallNumber)
    {
        CHECK_AND_CALL_HANDLER(execveat);
        CHECK_AND_CALL_HANDLER(execve);
        CHECK_AND_CALL_HANDLER(stat);
        CHECK_AND_CALL_HANDLER(lstat);
        CHECK_AND_CALL_HANDLER(fstat);
        CHECK_AND_CALL_HANDLER_NEW(fstatat);
        CHECK_AND_CALL_HANDLER(access);
        CHECK_AND_CALL_HANDLER(faccessat);
        CHECK_AND_CALL_HANDLER(creat);
        CHECK_AND_CALL_HANDLER(open);
        CHECK_AND_CALL_HANDLER(openat);
        CHECK_AND_CALL_HANDLER(write);
        CHECK_AND_CALL_HANDLER(writev);
        CHECK_AND_CALL_HANDLER(pwritev);
        CHECK_AND_CALL_HANDLER(pwritev2);
        CHECK_AND_CALL_HANDLER(pwrite64);
        CHECK_AND_CALL_HANDLER(truncate);
        CHECK_AND_CALL_HANDLER(ftruncate);
        CHECK_AND_CALL_HANDLER(rmdir);
        CHECK_AND_CALL_HANDLER(rename);
        CHECK_AND_CALL_HANDLER(renameat);
        CHECK_AND_CALL_HANDLER(link);
        CHECK_AND_CALL_HANDLER(linkat);
        CHECK_AND_CALL_HANDLER(unlink);
        CHECK_AND_CALL_HANDLER(unlinkat);
        CHECK_AND_CALL_HANDLER(symlink);
        CHECK_AND_CALL_HANDLER(symlinkat);
        CHECK_AND_CALL_HANDLER(readlink);
        CHECK_AND_CALL_HANDLER(readlinkat);
        CHECK_AND_CALL_HANDLER(utime);
        CHECK_AND_CALL_HANDLER(utimes);
        CHECK_AND_CALL_HANDLER(utimensat);
        CHECK_AND_CALL_HANDLER(futimesat);
        CHECK_AND_CALL_HANDLER(mkdir);
        CHECK_AND_CALL_HANDLER(mkdirat);
        CHECK_AND_CALL_HANDLER(mknod);
        CHECK_AND_CALL_HANDLER(mknodat);
        CHECK_AND_CALL_HANDLER(chmod);
        CHECK_AND_CALL_HANDLER(fchmod);
        CHECK_AND_CALL_HANDLER(fchmodat);
        CHECK_AND_CALL_HANDLER(chown);
        CHECK_AND_CALL_HANDLER(fchown);
        CHECK_AND_CALL_HANDLER(lchown);
        CHECK_AND_CALL_HANDLER(fchownat);
        CHECK_AND_CALL_HANDLER(sendfile);
        CHECK_AND_CALL_HANDLER(copy_file_range);
        CHECK_AND_CALL_HANDLER(name_to_handle_at);
        CHECK_AND_CALL_HANDLER(fork);
        CHECK_AND_CALL_HANDLER(clone);
        default:
            // This should not happen in theory with filtering enabled
            // However if it does occur, we can ignore this syscall and log a message for debugging if necessary
            BXL_LOG_DEBUG(m_bxl, "[PTrace] Unsupported syscall caught by ptrace '%d'", syscallNumber);
            break;
    }
}

void PTraceSandbox::ReportOpen(std::string path, int oflag, std::string syscallName)
{
    int status = 0;
    mode_t pathMode = m_bxl->get_mode(path.c_str());
    bool pathExists = pathMode != 0;
    bool isCreate = !pathExists && (oflag & (O_CREAT|O_TRUNC));
    bool isWrite = pathExists && (oflag & (O_CREAT|O_TRUNC) && ((oflag & O_ACCMODE == O_WRONLY) || (oflag & O_ACCMODE == O_RDWR)));

    IOEvent event(
        m_traceePid,
        /* cpid */ 0,
        /* ppid */ 0,
        isCreate ? ES_EVENT_TYPE_NOTIFY_CREATE : isWrite ? ES_EVENT_TYPE_NOTIFY_WRITE : ES_EVENT_TYPE_NOTIFY_OPEN,
        ES_ACTION_TYPE_NOTIFY,
        path,
        /* dest */ "",
        m_bxl->GetProgramPath(),
        pathMode,
        /* modified */ false,
        /* error */ 0
    );

    m_bxl->report_access(syscallName.c_str(), event);
}

void PTraceSandbox::ReportCreate(std::string syscallName, int dirfd, const char *pathname, mode_t mode, long returnValue, bool checkCache)
{
    IOEvent event(
        m_traceePid,
        /* cpid */ 0,
        /* ppid */ 0,
        ES_EVENT_TYPE_NOTIFY_CREATE,
        ES_ACTION_TYPE_NOTIFY,
        m_bxl->normalize_path_at(dirfd, pathname, /*oflags*/0, m_traceePid),
        /* dest */ "",
        m_bxl->GetProgramPath(),
        mode,
        /* modified */ false,
        /* error */ returnValue
    );
    
    m_bxl->report_access(syscallName.c_str(), event, checkCache);
}

std::vector<std::tuple<pid_t, std::string>>::iterator PTraceSandbox::FindProcess(pid_t pid)
{
    return std::find_if
    (
        m_traceeTable.begin(),
        m_traceeTable.end(),
        [pid](const std::tuple<pid_t, std::string>& item) { return std::get<0>(item) == pid; }
    );
}

void PTraceSandbox::UpdateTraceeTableForExec(std::string exePath)
{
    auto maybeProcess = FindProcess(m_traceePid);
    if (maybeProcess != m_traceeTable.end())
    {
        std::get<1>(maybeProcess[0]) = exePath;
    }
    else
    {
        // Special case for vfork
        // When vfork is called, the parent process is suspended until the child calls exec
        // So if we see a process here that calls exec that wasn't in the table of traced processes
        // then it is safe to assume that process was likely created by a vfork.
        // We have special handling for clone and fork so that this does not happen with those.
        // vfork is treated differently here because the handlers for clone/fork rely on the parent
        // process continuing to execute after the child process is spawned.
        // Since the parent is blocked, this will block the waitpid on the tracer which is explicitly waiting on the parent
        // to avoid reporting accesses before a process creation report is sent.
        // If this happens, the tracer will be blocked waiting for the parent process to be in SIGSTOP for the next ptrace event,
        // the parent will be blocked on the child process to execve
        // and the execve on the child will be blocked by a SIGSTOP from ptrace because the child is automatically traced by ptrace
        // which ptrace can't handle because it's blocked on the waitpid for the parent.
        IOEvent event(m_traceePid, m_traceePid, /* traceeppid */ 0, ES_EVENT_TYPE_NOTIFY_FORK, ES_ACTION_TYPE_NOTIFY, exePath, std::string(""), exePath, /* mode */ 0, false, /* error */ 0);
        m_bxl->report_access("vfork", event, /* checkCache */ false);
        m_traceeTable.push_back(std::make_tuple(m_traceePid, exePath));

        BXL_LOG_DEBUG(m_bxl, "[PTrace] Added new tracee with PID '%d'", m_traceePid);
    }
}

// Syscall Handlers
HANDLER_FUNCTION(execveat)
{
    // TODO: Is this syscall obsolete?
    int dirfd = ReadArgumentLong(1);
    std::string pathname = ReadArgumentString(SYSCALL_NAME_STRING(execveat), 2, /*nullTerminated*/ true);
    int flags = ReadArgumentLong(5);

    int oflags = (flags & AT_SYMLINK_NOFOLLOW) ? O_NOFOLLOW : 0;
    std::string exePath = m_bxl->normalize_path_at(dirfd, pathname.c_str(), oflags, m_traceePid);
    char mutableExePath[exePath.length() + 1];

    strcpy(mutableExePath, exePath.c_str());

    UpdateTraceeTableForExec(exePath);

    m_bxl->report_exec(SYSCALL_NAME_STRING(execveat), basename(mutableExePath), exePath.c_str(), /* error*/ 0, /* mode */ 0, m_traceePid);
    if (m_bxl->IsReportingProcessArgs()) {
        m_bxl->report_exec_args(m_traceePid, ReadArgumentVector(SYSCALL_NAME_STRING(execveat), /* argumentIndex */ 3).c_str());
    }
    
}

HANDLER_FUNCTION(execve)
{
    std::string file = ReadArgumentString(SYSCALL_NAME_STRING(execve), 1, /*nullTerminated*/ true);
    char mutableFilePath[file.length() + 1];

    strcpy(mutableFilePath, file.c_str());

    UpdateTraceeTableForExec(file);

    m_bxl->report_exec(SYSCALL_NAME_STRING(execve), basename(mutableFilePath), file.c_str(), /* error */ 0, /* mode */ 0, m_traceePid);
    if (m_bxl->IsReportingProcessArgs()) {
        m_bxl->report_exec_args(m_traceePid, ReadArgumentVector(SYSCALL_NAME_STRING(execve), /* argumentIndex */ 2).c_str());
    }
}

HANDLER_FUNCTION(stat)
{
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(stat), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(stat), ES_EVENT_TYPE_NOTIFY_STAT, pathname.c_str(), /*mode*/ 0, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(lstat)
{
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(lstat), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(lstat), ES_EVENT_TYPE_NOTIFY_STAT, pathname.c_str(), /*mode*/ 0, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(fstat)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(fstat), fd, ES_EVENT_TYPE_NOTIFY_STAT);
}

// NOTE: This stat function is not interposed by the Linux sandbox normally
// However, when calling stat, the final call to the kernel may be this one rather than stat which is why we intercept this
HANDLER_FUNCTION_NEW(fstatat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(fstatat), 2, /*nullTerminated*/ true);
    auto flags = ReadArgumentLong(4);

    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(fstatat), ES_EVENT_TYPE_NOTIFY_STAT, dirfd, pathname.c_str(), flags, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(access)
{
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(access), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(access), ES_EVENT_TYPE_NOTIFY_ACCESS, pathname.c_str(), /* mode */ 0U, /* flags */ 0, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(faccessat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(faccessat), 2, /*nullTerminated*/ true);
    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(faccessat), ES_EVENT_TYPE_NOTIFY_ACCESS, dirfd, pathname.c_str(), /*oflags*/ 0, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(creat)
{
    auto path = m_bxl->normalize_path(ReadArgumentString(SYSCALL_NAME_STRING(creat), 1, /*nullTerminated*/ true).c_str(), /*oflags*/0, m_traceePid);
    auto oflag = O_CREAT | O_WRONLY | O_TRUNC;
    ReportOpen(path, oflag, SYSCALL_NAME_STRING(creat));

}

HANDLER_FUNCTION(open)
{
    auto path = m_bxl->normalize_path(ReadArgumentString(SYSCALL_NAME_STRING(open), 1, /*nullTerminated*/ true).c_str(), /*oflags*/0, m_traceePid);
    auto oflag = ReadArgumentLong(2);
    ReportOpen(path, oflag, SYSCALL_NAME_STRING(open));
}

HANDLER_FUNCTION(openat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathName = ReadArgumentString(SYSCALL_NAME_STRING(openat), 2, /*nullTerminated*/ true);
    auto path = m_bxl->normalize_path_at(dirfd, pathName.c_str(), /*oflags*/0, m_traceePid);
    auto flags = ReadArgumentLong(3);
    ReportOpen(path, flags, SYSCALL_NAME_STRING(openat));
}

void PTraceSandbox::HandleReportAccessFd(const char *syscall, int fd, es_event_type_t event /*ES_EVENT_TYPE_NOTIFY_WRITE*/)
{
    auto path = m_bxl->fd_to_path(fd, m_traceePid);

    // Readlink returns type:[inode] if the path is not a file (files will return absolute paths)
    if (path[0] == '/')
    {
        m_bxl->report_access(syscall, event, path.c_str(), m_emptyStr, /*mode*/0, /* error */ 0, /* checkCache */ true, m_traceePid);
    }
}

HANDLER_FUNCTION(write)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(write), fd);
}

HANDLER_FUNCTION(writev)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(writev), fd);
}

HANDLER_FUNCTION(pwritev)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(pwritev), fd);
}

HANDLER_FUNCTION(pwritev2)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(pwritev2), fd);
}

HANDLER_FUNCTION(pwrite64)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(pwrite64), fd);
}

HANDLER_FUNCTION(truncate)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(truncate), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(truncate), ES_EVENT_TYPE_NOTIFY_WRITE, path.c_str(), /* mode */ 0, /* oflags */ 0, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(ftruncate)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(ftruncate), fd);
}

HANDLER_FUNCTION(rmdir)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(rmdir), 1, /*nullTerminated*/ true);

    // See comment about the need to propagate the returned value under HANDLER_FUNCTION(mkdir)
    int status = 0;
    ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
    waitpid(m_traceePid, &status, 0);

    // We don't want to use the cache since we want to distinguish between creation and deletion of directories
    m_bxl->report_access(SYSCALL_NAME_STRING(rmdir), ES_EVENT_TYPE_NOTIFY_UNLINK, path.c_str(), m_emptyStr, /*mode*/ S_IFDIR, /* error */ GetErrno(), /*checkCache */ false, m_traceePid);
}

HANDLER_FUNCTION(rename)
{
    auto oldpath = ReadArgumentString(SYSCALL_NAME_STRING(rename), 1, /*nullTerminated*/ true);
    auto newpath = ReadArgumentString(SYSCALL_NAME_STRING(rename), 2, /*nullTerminated*/ true);

    HandleRenameGeneric(SYSCALL_NAME_STRING(rename), AT_FDCWD, oldpath.c_str(), AT_FDCWD, newpath.c_str());
}

HANDLER_FUNCTION(renameat)
{
    auto olddirfd = ReadArgumentLong(1);
    auto oldpath = ReadArgumentString(SYSCALL_NAME_STRING(renameat), 2, /*nullTerminated*/ true);
    auto newdirfd = ReadArgumentLong(3);
    auto newpath = ReadArgumentString(SYSCALL_NAME_STRING(renameat), 4, /*nullTerminated*/ true);
    
    HandleRenameGeneric(SYSCALL_NAME_STRING(renameat), olddirfd, oldpath.c_str(), newdirfd, newpath.c_str());
}

HANDLER_FUNCTION(renameat2)
{
    auto olddirfd = ReadArgumentLong(1);
    auto oldpath = ReadArgumentString(SYSCALL_NAME_STRING(renameat2), 2, /*nullTerminated*/ true);
    auto newdirfd = ReadArgumentLong(3);
    auto newpath = ReadArgumentString(SYSCALL_NAME_STRING(renameat2), 4, /*nullTerminated*/ true);
    
    HandleRenameGeneric(SYSCALL_NAME_STRING(renameat2), olddirfd, oldpath.c_str(), newdirfd, newpath.c_str());
}

void PTraceSandbox::HandleRenameGeneric(const char *syscall, int olddirfd, const char *oldpath, int newdirfd, const char *newpath)
{
    string oldStr = m_bxl->normalize_path_at(olddirfd, oldpath, O_NOFOLLOW, m_traceePid);
    string newStr = m_bxl->normalize_path_at(newdirfd, newpath, O_NOFOLLOW, m_traceePid);

    mode_t mode = m_bxl->get_mode(oldStr.c_str());    
    std::vector<std::string> filesAndDirectories;
    
    if (S_ISDIR(mode))
    {
        bool enumerateResult = m_bxl->EnumerateDirectory(oldStr, /*recursive*/ true, filesAndDirectories);
        if (enumerateResult)
        {
            for (auto fileOrDirectory : filesAndDirectories)
            {
                // Source
                auto mode = m_bxl->get_mode(fileOrDirectory.c_str());
                m_bxl->report_access(syscall, ES_EVENT_TYPE_NOTIFY_UNLINK, fileOrDirectory.c_str(), mode, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);

                // Destination
                fileOrDirectory.replace(0, oldStr.length(), newStr);
                ReportOpen(fileOrDirectory, O_CREAT, std::string(syscall));
            }
        }
    }
    else
    {
        auto mode = m_bxl->get_mode(oldStr.c_str());
        // Source
        m_bxl->report_access(syscall, ES_EVENT_TYPE_NOTIFY_UNLINK, oldStr.c_str(), mode, O_NOFOLLOW, /* error*/ 0, /* checkCache */ true, m_traceePid);

        // Destination
        ReportOpen(newStr, O_CREAT, std::string(syscall));
    }
}

HANDLER_FUNCTION(link)
{
    auto oldpath = ReadArgumentString(SYSCALL_NAME_STRING(link), 1, /*nullTerminated*/ true);
    auto newpath = ReadArgumentString(SYSCALL_NAME_STRING(link), 2, /*nullTerminated*/ true);

    m_bxl->report_access(
        SYSCALL_NAME_STRING(link),
        ES_EVENT_TYPE_NOTIFY_LINK,
        m_bxl->normalize_path(oldpath.c_str(), O_NOFOLLOW, m_traceePid).c_str(),
        m_bxl->normalize_path(newpath.c_str(), O_NOFOLLOW, m_traceePid).c_str(), 
        /* mode */ 0,
        /* error */ 0,
        /* checkCache */ true,
        m_traceePid);
}

HANDLER_FUNCTION(linkat)
{
    auto olddirfd = ReadArgumentLong(1);
    auto oldpath = ReadArgumentString(SYSCALL_NAME_STRING(linkat), 2, /*nullTerminated*/ true);
    auto newdirfd = ReadArgumentLong(3);
    auto newpath = ReadArgumentString(SYSCALL_NAME_STRING(linkat), 4, /*nullTerminated*/ true);

    m_bxl->report_access(
        SYSCALL_NAME_STRING(linkat),
        ES_EVENT_TYPE_NOTIFY_LINK,
        m_bxl->normalize_path_at(olddirfd, oldpath.c_str(), O_NOFOLLOW, m_traceePid).c_str(),
        m_bxl->normalize_path_at(newdirfd, newpath.c_str(), O_NOFOLLOW, m_traceePid).c_str(),
        /* mode */ 0,
        /* error */ 0,
        /* checkCache */ true,
        m_traceePid);
}

HANDLER_FUNCTION(unlink)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(unlink), 1, /*nullTerminated*/ true);

    if (path[0] != '\0')
    {
        m_bxl->report_access(SYSCALL_NAME_STRING(unlink), ES_EVENT_TYPE_NOTIFY_UNLINK, path.c_str(), /*mode*/ 0, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);
    }
}

HANDLER_FUNCTION(unlinkat)
{
    auto dirfd = ReadArgumentLong(1);
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(unlinkat), 2, /*nullTerminated*/ true);
    auto flags = ReadArgumentLong(3);

    if (dirfd != AT_FDCWD && path[0] != '\0')
    {
        int oflags = (flags & AT_REMOVEDIR) ? 0 : O_NOFOLLOW;
        // TODO: Figure out how to report the errno
        m_bxl->report_access_at(SYSCALL_NAME_STRING(unlinkat), ES_EVENT_TYPE_NOTIFY_UNLINK, dirfd, path.c_str(), oflags, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
    }
}

HANDLER_FUNCTION(symlink)
{
    auto linkPath = ReadArgumentString(SYSCALL_NAME_STRING(symlink), 2, /*nullTerminated*/ true);

    // TODO: Figure out how to report the errno
    IOEvent event(
        m_traceePid,
        /* cpid */ 0,
        /* ppid */ 0,
        ES_EVENT_TYPE_NOTIFY_CREATE, 
        ES_ACTION_TYPE_NOTIFY,
        m_bxl->normalize_path(linkPath.c_str(), O_NOFOLLOW, m_traceePid),
        /* dest */ "",
        m_bxl->GetProgramPath(),
        S_IFLNK,
        /* modified */ false,
        /* error */ 0
    );
    
    m_bxl->report_access(SYSCALL_NAME_STRING(symlink), event);
}

HANDLER_FUNCTION(symlinkat)
{
    auto dirfd = ReadArgumentLong(2);
    auto linkPath = ReadArgumentString(SYSCALL_NAME_STRING(symlinkat), 3, /*nullTerminated*/ true);

    // TODO: Figure out how to report the errno
    IOEvent event(
        m_traceePid,
        /* cpid */ 0,
        /* ppid */ 0,
        ES_EVENT_TYPE_NOTIFY_CREATE, 
        ES_ACTION_TYPE_NOTIFY,
        m_bxl->normalize_path_at(dirfd, linkPath.c_str(), O_NOFOLLOW, m_traceePid),
        /* dest */ "",
        m_bxl->GetProgramPath(),
        S_IFLNK,
        /* modified */ false,
        /* error */ 0
    );
    
    m_bxl->report_access(SYSCALL_NAME_STRING(symlinkat), event);

}

HANDLER_FUNCTION(readlink)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(readlink), 1, /*nullTerminated*/ true);

    m_bxl->report_access(SYSCALL_NAME_STRING(readlink), ES_EVENT_TYPE_NOTIFY_READLINK, path.c_str(), /*mode*/ 0, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(readlinkat)
{
    auto fd = ReadArgumentLong(1);
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(readlinkat), 2, /*nullTerminated*/ true);

    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(readlinkat), ES_EVENT_TYPE_NOTIFY_READLINK, fd, path.c_str(), O_NOFOLLOW, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(utime)
{
    auto filename = ReadArgumentString(SYSCALL_NAME_STRING(utime), 1, /*nullTerminated*/ true);

    m_bxl->report_access(SYSCALL_NAME_STRING(utime), ES_EVENT_TYPE_NOTIFY_SETTIME, filename.c_str(), "", /* mode */ 0, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(utimes)
{
    Handleutime();
}

HANDLER_FUNCTION(utimensat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(utimensat), 2, /*nullTerminated*/ true);

    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(utimensat), ES_EVENT_TYPE_NOTIFY_SETTIME, dirfd, pathname.c_str(), /*oflags*/ 0, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(futimesat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(futimesat), 2, /*nullTerminated*/ true);

    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(futimesat), ES_EVENT_TYPE_NOTIFY_SETTIME, dirfd, pathname.c_str(), /*oflags*/ 0, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(mkdir)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(mkdir), 1, /*nullTerminated*/ true);

    // For mkdir (also for rmdir and mkdirat) we want to report the return value of the function as part of the
    // report since on managed side bxl needs to understand whether the directory creation succeeded.
    // This is used to determine whether a directory was created by the build, which is an input for 
    // optimizations related to computing directory fingerprints in ObserverdInputProcessor
    int status = 0;
    ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
    waitpid(m_traceePid, &status, 0);

    // We don't want to use the cache since we want to distinguish between creation and deletion of directories
    ReportCreate(SYSCALL_NAME_STRING(mkdir), AT_FDCWD, path.c_str(), S_IFDIR, GetErrno(), /* checkCache */ false);
}

HANDLER_FUNCTION(mkdirat)
{
    auto dirfd = ReadArgumentLong(1);
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(mkdirat), 2, /*nullTerminated*/ true);

    // See comment about the need to propagate the returned value under HANDLER_FUNCTION(mkdir)
    int status = 0;
    ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
    waitpid(m_traceePid, &status, 0);

    // We don't want to use the cache since we want to distinguish between creation and deletion of directories
    ReportCreate(SYSCALL_NAME_STRING(mkdirat), dirfd, path.c_str(), S_IFDIR, GetErrno(), /* checkCache */ false);
}

HANDLER_FUNCTION(mknod)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(mknod), 1, /*nullTerminated*/ true);

    ReportCreate(SYSCALL_NAME_STRING(mknod), AT_FDCWD, path.c_str(), S_IFREG);
}

HANDLER_FUNCTION(mknodat)
{
    auto dirfd = ReadArgumentLong(1);
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(mknodat), 2, /*nullTerminated*/ true);

    ReportCreate(SYSCALL_NAME_STRING(mknodat), dirfd, path.c_str(), S_IFREG);

}

HANDLER_FUNCTION(chmod)
{
    auto path = ReadArgumentString(SYSCALL_NAME_STRING(chmod), 1, /*nullTerminated*/ true);

    m_bxl->report_access(SYSCALL_NAME_STRING(chmod), ES_EVENT_TYPE_NOTIFY_SETMODE, path.c_str(), /* mode */ 0, /* oflags */ 0, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(fchmod)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(fchmod), fd, ES_EVENT_TYPE_NOTIFY_SETMODE);
}

HANDLER_FUNCTION(fchmodat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(fchmodat), 2, /*nullTerminated*/ true);
    auto flags = ReadArgumentLong(4);

    int oflags = (flags & AT_SYMLINK_NOFOLLOW) ? O_NOFOLLOW : 0;
    // TODO: Figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(fchmodat), ES_EVENT_TYPE_NOTIFY_SETMODE, dirfd, pathname.c_str(), oflags, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(chown)
{
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(chown), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(chown), ES_EVENT_TYPE_AUTH_SETOWNER, pathname.c_str(), "", /* mode */ 0, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(fchown)
{
    auto fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(fchown), fd, ES_EVENT_TYPE_AUTH_SETOWNER);
}

HANDLER_FUNCTION(lchown)
{
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(lchown), 1, /*nullTerminated*/ true);
    m_bxl->report_access(SYSCALL_NAME_STRING(lchown), ES_EVENT_TYPE_AUTH_SETOWNER, pathname.c_str(), /*mode*/ 0, O_NOFOLLOW, /* error */ 0, /* checkCache */ true, m_traceePid);
}

HANDLER_FUNCTION(fchownat)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(fchownat), 2, /*nullTerminated*/ true);
    auto flags = ReadArgumentLong(5);

    int oflags = (flags & AT_SYMLINK_NOFOLLOW) ? O_NOFOLLOW : 0;
    // TODO: figure out how to report the errno
    m_bxl->report_access_at(SYSCALL_NAME_STRING(fchownat), ES_EVENT_TYPE_AUTH_SETOWNER, dirfd, pathname.c_str(), oflags, /*getModeWithFd*/ false, m_traceePid, /* error */ 0);
}

HANDLER_FUNCTION(sendfile)
{
    auto out_fd = ReadArgumentLong(1);
    HandleReportAccessFd(SYSCALL_NAME_STRING(sendfile), out_fd);
}

HANDLER_FUNCTION(copy_file_range)
{
    auto fd_out = ReadArgumentLong(3);
    HandleReportAccessFd(SYSCALL_NAME_STRING(copy_file_range), fd_out);
}

HANDLER_FUNCTION(name_to_handle_at)
{
    auto dirfd = ReadArgumentLong(1);
    auto pathname = ReadArgumentString(SYSCALL_NAME_STRING(name_to_handle_at), 2, /*nullTerminated*/ true);
    auto flags = ReadArgumentLong(5);

    int oflags = (flags & AT_SYMLINK_FOLLOW) ? 0 : O_NOFOLLOW;
    string pathStr = m_bxl->normalize_path_at(dirfd, pathname.c_str(), oflags, m_traceePid);
    ReportOpen(pathStr, oflags, SYSCALL_NAME_STRING(name_to_handle_at));
}

void PTraceSandbox::HandleChildProcess(const char *syscall)
{
    int status = 0;
    ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
    waitpid(m_traceePid, &status, 0);

    if (status >> 8 == (SIGTRAP | (PTRACE_EVENT_CLONE << 8))
        || status >> 8 == (SIGTRAP | (PTRACE_EVENT_FORK << 8)))
    {
        ptrace(PTRACE_SYSCALL, m_traceePid, NULL, NULL);
        waitpid(m_traceePid, &status, 0);
    }
    
    long childpid = ReadArgumentLong(0);

    // Find the parent pid for this tracee
    auto maybeParent = FindProcess(m_traceePid);
    std::string exePath;
    
    // Best effort to get the ppid/exe of the tracee here. There's no nice way to do this from outside the process
    if (maybeParent != m_traceeTable.end())
    {
        exePath = std::get<1>(maybeParent[0]);
    }
    else
    {
        // This case isn't expected to happen as long as ptrace works properly, but in case it does, we will report 0 as the ppid.
        exePath = m_bxl->GetProgramPath();
    }

    IOEvent event(m_traceePid, childpid, /* traceeppid */ 0, ES_EVENT_TYPE_NOTIFY_FORK, ES_ACTION_TYPE_NOTIFY, exePath, std::string(""), exePath, /* mode */ 0, false, /* error */ 0);
    m_bxl->report_access(syscall, event, /* checkCache */ false);

    // Record the new child tracee
    // When PTRACE_O_TRACEFORK/CLONE/VFORK is set, the child process is automatically ptraced as well
    m_traceeTable.push_back(std::make_tuple(childpid, exePath));

    BXL_LOG_DEBUG(m_bxl, "[PTrace] Added new tracee with PID '%d'", childpid);
}

HANDLER_FUNCTION(fork)
{
    HandleChildProcess(SYSCALL_NAME_STRING(fork));
}

HANDLER_FUNCTION(clone)
{
    HandleChildProcess(SYSCALL_NAME_STRING(clone));
}

HANDLER_FUNCTION(exit)
{
    m_bxl->SendExitReport(m_traceePid);
}