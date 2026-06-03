using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ChoreBuddy.TestApp;

/** Launches a process inside the interactive desktop session.
 *
 *  The agent runs as LocalSystem in session 0, which has no visible desktop.
 *  A plain Process.Start there would spawn the target invisibly in session 0.
 *  To put an app in front of the kid we grab the active console session's user
 *  token (WTSQueryUserToken) and CreateProcessAsUser with it.
 *
 *  When the agent is instead running interactively (dev box, not as SYSTEM)
 *  WTSQueryUserToken is denied; we fall back to a normal Process.Start, which
 *  is correct because we're already in the user's session. */
public static class ProcessLauncher
{
    public static bool LaunchInActiveSession(string exePath, Action<string> log)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            log($"LaunchInActiveSession: path missing: {exePath}");
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            log("LaunchInActiveSession: no active console session");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            // Denied means we're not SYSTEM -> we are the interactive user already.
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_PRIVILEGE_NOT_HELD || err == ERROR_ACCESS_DENIED)
            {
                log($"LaunchInActiveSession: not SYSTEM (err {err}), launching directly");
                return LaunchDirect(exePath, log);
            }
            log($"LaunchInActiveSession: WTSQueryUserToken failed (err {err})");
            return false;
        }

        var primaryToken = IntPtr.Zero;
        var envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                log($"LaunchInActiveSession: DuplicateTokenEx failed (err {Marshal.GetLastWin32Error()})");
                return false;
            }

            CreateEnvironmentBlock(out envBlock, primaryToken, false);

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";

            var workingDir = Path.GetDirectoryName(exePath) ?? Environment.SystemDirectory;
            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            // Quote the path; commandLine must be a writable buffer for the API.
            var commandLine = $"\"{exePath}\"";

            if (!CreateProcessAsUser(primaryToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, workingDir,
                    ref si, out var pi))
            {
                log($"LaunchInActiveSession: CreateProcessAsUser failed (err {Marshal.GetLastWin32Error()})");
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log($"LaunchInActiveSession: started {Path.GetFileName(exePath)} in session {sessionId}");
            return true;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    static bool LaunchDirect(string exePath, Action<string> log)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.SystemDirectory,
                UseShellExecute = true
            });
            log($"LaunchDirect: started {Path.GetFileName(exePath)}");
            return true;
        }
        catch (Exception ex)
        {
            log($"LaunchDirect failed: {ex.Message}");
            return false;
        }
    }

    // ----- Win32 interop -----

    const int ERROR_ACCESS_DENIED = 5;
    const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NEW_CONSOLE = 0x00000010;
    const int SecurityImpersonation = 2;
    const int TokenPrimary = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
        ref SECURITY_ATTRIBUTES tokenAttributes, int impersonationLevel,
        int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr envBlock, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr envBlock);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? applicationName,
        string? commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
