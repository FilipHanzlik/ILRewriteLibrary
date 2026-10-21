using System.Diagnostics;

namespace ValidationTests;

public sealed record StripperExecutionResult(
    bool Success,
    int ExitCode,
    long ElapsedMilliseconds,
    string StandardOutput,
    string StandardError);

public static class StripperProcessRunner
{
    public static StripperExecutionResult Run(
        string stripperPath,
        string inputPath,
        string outputPath,
        string argumentTemplate = "{input} {output}",
        int timeoutSeconds = 60)
    {
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);

        string outputDir = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        string argsString = argumentTemplate
            .Replace("{input}", $"\"{inputPath}\"")
            .Replace("{output}", $"\"{outputPath}\"");

        ProcessStartInfo startInfo = new()
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (stripperPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.Arguments = $"\"{stripperPath}\" {argsString}";
        }
        else
        {
            startInfo.FileName = stripperPath;
            startInfo.Arguments = argsString;
        }

        Stopwatch sw = Stopwatch.StartNew();
        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
            bool completed = process.WaitForExit(timeoutSeconds * 1000);
            sw.Stop();

            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                return new StripperExecutionResult(
                    false,
                    -1,
                    sw.ElapsedMilliseconds,
                    "",
                    $"Process timed out after {timeoutSeconds} seconds.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            return new StripperExecutionResult(
                process.ExitCode == 0,
                process.ExitCode,
                sw.ElapsedMilliseconds,
                stdout,
                stderr);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new StripperExecutionResult(
                false,
                -1,
                sw.ElapsedMilliseconds,
                "",
                $"Exception launching process '{startInfo.FileName} {startInfo.Arguments}': {ex.Message}");
        }
    }
}
