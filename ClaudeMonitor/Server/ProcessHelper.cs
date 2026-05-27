using System.Diagnostics;

namespace ClaudeMonitor.Server;

public static class ProcessHelper
{
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
