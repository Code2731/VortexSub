using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class CaptureDump
{
    internal sealed record Result(string Path, int WorkerProcessId, bool Written, string? Error, double ElapsedSeconds);

    // Run DbgHelp in a separate helper so dump collection cannot block cleanup indefinitely.
    public static async Task<Result> Collect(Process worker, string path)
    {
        var clock = Stopwatch.StartNew();
        var written = false;
        string? error = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Dump helper executable not found");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--dump-worker");
            start.ArgumentList.Add(worker.Id.ToString());
            start.ArgumentList.Add(path);
            using var helper = Process.Start(start) ?? throw new IOException("Dump helper did not start");
            try
            {
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                written = helper.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
                if (!written) error = "DUMP_HELPER_EXIT_" + helper.ExitCode;
            }
            catch (TimeoutException)
            {
                error = "DUMP_HELPER_TIMEOUT";
                if (!helper.HasExited) helper.Kill(entireProcessTree: true);
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception failure) { error ??= failure.Message; }
        return new Result(path, worker.Id, written, error, clock.Elapsed.TotalSeconds);
    }

    public static void Write(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3 || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("--dump-worker PID absolute-path required on Windows");
        using var worker = Process.GetProcessById(int.Parse(args[1]));
        using var file = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        // MiniDumpNormal | MiniDumpWithThreadInfo: thread stacks/metadata, no full-memory dump.
        if (!MiniDumpWriteDump(worker.Handle, (uint)worker.Id, file.SafeFileHandle.DangerousGetHandle(),
                0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            throw new IOException($"MiniDumpWriteDump failed: 0x{Marshal.GetLastWin32Error():X8}");
        file.Flush(flushToDisk: true);
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file,
        uint dumpType, IntPtr exceptionInfo, IntPtr userStream, IntPtr callback);
}
