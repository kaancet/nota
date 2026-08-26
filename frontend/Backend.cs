using System;
using System.Diagnostics;
using System.Text.Json;


namespace Nota;

// Owns the Python `nota.server` process and talks to it.
// IDisposable = "I hold a resource that must be released" -> callers invoke Dispose() when done.
public class Backend : IDisposable
{
    // M4 shortcut: hard-coded paths to this machine's venv + repo (we'll make these configurable later)
    private const string PythonPath = "/Users/kaan/code/nota/.venv/bin/python";
    private const string RepoPath = "/Users/kaan/code/nota";

    // a FIELD: belongs to the object and lives as long as the object does.
    // `readonly` = assigned once (in the constructor) and never reassigned afterwards.
    private readonly Process _process;

    // the CONSTRUCTOR: runs when you write `new Backend()`. Launches Python once.
    public Backend()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PythonPath,
            Arguments = "-m nota.server",
            WorkingDirectory = RepoPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        _process = Process.Start(startInfo)!;   // stored in the field -> stays alive with this object
    }


    public JsonElement Request(string method, object? parameters = null)
    {
        // build the JSON request from an object (@params -> the "params" key the server reads)
        string request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters });

        _process.StandardInput.WriteLine(request);   // send one line
        _process.StandardInput.Flush();

        string? line = _process.StandardOutput.ReadLine();   // read one line back (blocks until it arrives)
        if (line is null)
            throw new InvalidOperationException("backend closed the connection (no response)");
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();   // Clone so it survives after `doc` is disposed at method end
    }

    // Shut the backend down cleanly. Called when the app closes.
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();        // EOF -> server's `for line in sys.stdin` loop ends
                if (!_process.WaitForExit(1000))       // give it up to 1 second to exit on its own
                    _process.Kill();                   // still alive? force it
            }
        }
        catch { /* process already gone -- nothing to do */ }
        _process.Dispose();                            // release the OS handle
    }
}

