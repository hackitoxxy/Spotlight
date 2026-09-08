[CmdletBinding()]
param([Parameter(Mandatory = $true)][string[]]$ArchivePath)
$ErrorActionPreference = 'Stop'
# Diagnostic only: Windows lists applications using these files. No files are opened for
# writing, and no process is stopped or restarted. An empty list cannot rule out transient locks.
# API reference: https://learn.microsoft.com/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist
if (-not ('ArchiveUsers' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class ArchiveUsers
{
    [StructLayout(LayoutKind.Sequential)]
    public struct UniqueProcess { public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint count, string[] paths, uint applications, IntPtr processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, out uint reasons);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);
    public static ProcessInfo[] Query(string[] paths)
    {
        uint session;
        Check(RmStartSession(out session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, IntPtr.Zero));
            uint count = 0, needed, reasons;
            ProcessInfo[] result = null;
            for (int retry = 0; retry < 5; retry++)
            {
                int status = RmGetList(session, out needed, ref count, result, out reasons);
                if (status == 0) { Array.Resize(ref result, (int)count); return result; }
                if (status != 234) Check(status);
                count = needed;
                result = new ProcessInfo[count];
            }
            throw new InvalidOperationException("File users changed during the query; retry.");
        }
        finally { RmEndSession(session); }
    }
    static void Check(int status) { if (status != 0) throw new Win32Exception(status); }
}
'@
}
foreach ($archive in $ArchivePath) {
    $resolvedArchive = (Resolve-Path -LiteralPath $archive).Path
    $users = @([ArchiveUsers]::Query(@($resolvedArchive)))
    [pscustomobject]@{
        Path = $resolvedArchive
        Users = ($users | ForEach-Object { '{0} (PID {1})' -f $_.Name, $_.Process.Id }) -join ', '
    }
}
