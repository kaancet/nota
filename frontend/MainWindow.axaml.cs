using System;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;

namespace Nota;

public partial class MainWindow : Window
{
    // M3 shortcut: hard-coded paths to this machine's venv + repo. We'll make these
    // configurable later so the app isn't tied to one absolute location.
    private const string PythonPath = "/Users/kaan/code/nota/.venv/bin/python";
    private const string RepoPath = "/Users/kaan/code/nota";

    public MainWindow()
    {
        InitializeComponent();
        Handshake();
    }

    private void Handshake()
    {
        try
        {
            // describe how to launch the Python backend (like subprocess.Popen args)
            var startInfo = new ProcessStartInfo
            {
                FileName = PythonPath,
                Arguments = "-m nota.server",
                WorkingDirectory = RepoPath,
                RedirectStandardInput = true,   // capture its stdin so we can write to it
                RedirectStandardOutput = true,  // capture its stdout so we can read from it
                UseShellExecute = false,        // required when redirecting; run it directly
            };

            var process = Process.Start(startInfo)!;   // launch; `!` = "trust me, not null"

            // send one JSON request line, then push it through immediately
            process.StandardInput.WriteLine("{\"id\":1,\"method\":\"server_info\"}");
            process.StandardInput.Flush();

            // read one JSON response line back (blocks until it arrives)
            string? line = process.StandardOutput.ReadLine();

            // parse the JSON and walk to result.protocol_version
            using var doc = JsonDocument.Parse(line!);
            string? version = doc.RootElement
                .GetProperty("result")
                .GetProperty("protocol_version")
                .GetString();

            StatusText.Text = $"connected — protocol {version}";

            process.StandardInput.Close();  // closing stdin ends the server's read loop -> it exits
        }
        catch (Exception ex)
        {
            StatusText.Text = $"backend error: {ex.Message}";
        }
    }
}
