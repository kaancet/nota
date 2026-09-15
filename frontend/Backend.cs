using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;


namespace Nota;

public class Backend : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter? _errLog;   // backend stderr -> a log file, so a crash is diagnosable

    public Backend()
    {
        var (python, repo) = Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m nota.server",
            WorkingDirectory = repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start: {python}");

        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nota", "logs");
            Directory.CreateDirectory(logDir);
            _errLog = new StreamWriter(Path.Combine(logDir, "backend-stderr.log"), append: true) { AutoFlush = true };
        }
        catch { _errLog = null; /* logging is best-effort; never block startup */ }

        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _errLog?.WriteLine(e.Data); };
        _process.BeginErrorReadLine();
    }

    private static (string python, string repo) Resolve()
    {
        // 1. env vars override everything
        string? envPy = Environment.GetEnvironmentVariable("NOTA_PYTHON");
        string? envRepo = Environment.GetEnvironmentVariable("NOTA_REPO");
        if (envPy != null && envRepo != null)
            return (envPy, envRepo);

        // 2. walk up from the exe dir to find pyproject.toml
        string? repo = envRepo;
        if (repo == null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml")))
                {
                    repo = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }
        }
        if (repo == null)
            throw new InvalidOperationException(
                "cannot find repo (no pyproject.toml above exe dir). Set NOTA_REPO env var.");

        string python = envPy ?? Path.Combine(repo, ".venv",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts" : "bin", "python");

        if (!File.Exists(python))
            throw new InvalidOperationException($"python not found at {python}. Set NOTA_PYTHON env var.");

        return (python, repo);
    }


    // A crashed backend must not hang the UI on ReadLine forever. If it has already exited, fail
    // loudly; when a timeout is given (the handshake), bound the wait. Long runs pass no timeout.
    public JsonElement Request(string method, object? parameters = null, TimeSpan? timeout = null)
    {
        if (_process.HasExited)
            throw new InvalidOperationException(
                $"backend exited (code {_process.ExitCode}); see ~/.nota/logs/backend-stderr.log");

        string request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters });
        _process.StandardInput.WriteLine(request);
        _process.StandardInput.Flush();

        string? line;
        if (timeout is TimeSpan t)
        {
            var task = Task.Run(() => _process.StandardOutput.ReadLine());
            if (!task.Wait(t))
                throw new TimeoutException(
                    $"backend did not answer within {t.TotalSeconds:0}s; see ~/.nota/logs/backend-stderr.log");
            line = task.Result;
        }
        else
        {
            line = _process.StandardOutput.ReadLine();
        }

        if (line is null)
            throw new InvalidOperationException("backend closed the connection (no response)");
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1000))
                    _process.Kill();
            }
        }
        catch { /* process already gone */ }
        _errLog?.Dispose();
        _process.Dispose();
    }
}
