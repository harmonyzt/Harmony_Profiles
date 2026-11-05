using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SPTarkov.Common.Models.Logging;

namespace PBT.Server;

internal sealed class LoadProgress : IDisposable
{
    private const string Reset = "\x1b[0m";
    private const string FrameColor = "\x1b[36m";
    private const string CountColor = "\x1b[32m";
    private const string DimColor = "\x1b[2m";

    private static readonly string[] Braille = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] Ascii = ["|", "/", "-", "\\"];
    private static readonly object renderGate = new();

    private readonly ISptLogger<PopulationStore> logger;
    private readonly string label;
    private readonly int total;
    private readonly string[] frames = Ascii;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim stop = new(false);
    private readonly Thread? spinner;
    private readonly bool color;
    private int done;
    private int lastStep;
    private int lastLength;
    private bool reported;
    private bool stopped;

    public LoadProgress(ISptLogger<PopulationStore> logger, string label, int total)
    {
        this.logger = logger;
        this.label = label;
        this.total = Math.Max(1, total);
        if (total <= 0 || !CanAnimate()) return;

        frames = UseBraille() ? Braille : Ascii;
        color = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && EnableAnsi();
        spinner = new Thread(Spin) { IsBackground = true, Name = "PBT load spinner" };
        spinner.Start();
    }

    public void Step()
    {
        int current = Interlocked.Increment(ref done);
        if (spinner is not null) return;

        int step = current * 4 / total;
        if (step == lastStep || clock.ElapsedMilliseconds < 5000) return;
        lastStep = step;
        reported = true;
        logger.Info($"[PBT] {label}: {current}/{total} ({step * 25}%)");
    }

    public void Finish()
    {
        if (stopped) return;
        StopSpinner();
        if (reported || (spinner is not null && clock.ElapsedMilliseconds >= 1000))
            logger.Info($"[PBT] {label}: done in {clock.Elapsed.TotalSeconds:0.0}s");
    }

    public void Dispose()
    {
        if (!stopped) StopSpinner();
        stop.Dispose();
    }

    private void Spin()
    {
        int frame = 0;
        while (!stop.Wait(100))
        {
            int current = Volatile.Read(ref done);
            int percent = current * 100 / total;
            double seconds = clock.Elapsed.TotalSeconds;
            string plain = $"{frames[frame]} {label}: {current}/{total} ({percent}%) {seconds:0.0}s";
            string line = color
                ? $"{FrameColor}{frames[frame]}{Reset} {FrameColor}{label}{Reset}: {CountColor}{current}/{total}{Reset} ({DimColor}{percent}%{Reset}) {DimColor}{seconds:0.0}s{Reset}"
                : plain;
            Render(line, plain);
            frame = (frame + 1) % frames.Length;
        }
    }

    private void Render(string line, string plain)
    {
        lock (renderGate)
        {
            int padding = Math.Max(0, lastLength - plain.Length);
            Console.Write('\r' + line + (padding > 0 ? new string(' ', padding) : ""));
            lastLength = plain.Length;
        }
    }

    private void StopSpinner()
    {
        stopped = true;
        stop.Set();
        spinner?.Join();
        if (spinner is null) return;

        lock (renderGate)
        {
            Console.Write('\r' + new string(' ', lastLength) + '\r');
        }
    }

    // downgrade to ASCII
    private static bool UseBraille()
    {
        try
        {
            if (Console.OutputEncoding.GetString(Console.OutputEncoding.GetBytes(Braille[0])) == Braille[0]) return true;
            Console.OutputEncoding = Encoding.UTF8;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Windows consoles need VT processing before raw ANSI colors render
    private static bool EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return !IsDumbTerminal();
        try
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            return GetConsoleMode(handle, out uint mode) && SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Needs a terminal that understand ANSI
    private static bool CanAnimate() => !Console.IsOutputRedirected && (OperatingSystem.IsWindows() || !IsDumbTerminal());

    private static bool IsDumbTerminal()
    {
        string? term = Environment.GetEnvironmentVariable("TERM");
        return string.IsNullOrWhiteSpace(term) || term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
#pragma warning restore SYSLIB1054
}
