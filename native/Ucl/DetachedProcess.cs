using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityCompat.Cli;

internal static partial class Program
{
    // Process.Start on Windows can pass other inheritable handles to the worker,
    // even when its own standard streams are redirected. Disable handle inheritance
    // at CreateProcess so a detached daemon cannot keep the caller's capture pipe open.
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        internal uint cb;
        internal IntPtr reserved, desktop, title;
        internal uint x,y,xSize,ySize,xChars,yChars,fill,flags;
        internal ushort show,reservedSize;
        internal IntPtr reservedBytes,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo {internal IntPtr process,thread;internal uint pid,tid;}
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDetached(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,uint flags,IntPtr environment,string directory,
        ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
    static string WindowsArgument(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\') {slashes++;continue;}
            result.Append('\\',c=='\"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        result.Append('\\',slashes*2);return result.Append('"').ToString();
    }
    static int StartDetached(string project)
    {
        string exe=Environment.ProcessPath!;
        var command=new StringBuilder(String.Join(" ",new[]{exe,"daemon","serve","--project",project}.Select(WindowsArgument)));
        var startup=new StartupInfo{cb=(uint)Marshal.SizeOf<StartupInfo>()};
        if(!CreateDetached(exe,command,IntPtr.Zero,IntPtr.Zero,false,0x00000008,IntPtr.Zero,project,ref startup,out var child))
            throw new Failure("DAEMON_START_FAILED",new Win32Exception(Marshal.GetLastWin32Error()).Message,4);
        CloseHandle(child.thread);CloseHandle(child.process);return checked((int)child.pid);
    }
}
