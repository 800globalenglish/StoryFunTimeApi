using System.Diagnostics;

namespace StoryFunTimeApi.Services;

public class TranscriptionService
{
    private readonly string _modelPath;

    public TranscriptionService(IConfiguration configuration)
    {
        _modelPath = configuration["Whisper:ModelPath"] ?? "whisper-models/ggml-base.en.bin";
    }

    public async Task<string> Transcribe(string audioFilePath)
    {
        var tempDir = "temp_transcripts";
        Directory.CreateDirectory(tempDir);
        var outputTextPath = $"{tempDir}/transcript_{Guid.NewGuid()}.txt";

        var args = $"-i \"{audioFilePath}\" -vn -af \"whisper=model={_modelPath}:language=en:queue=3:destination={outputTextPath}:format=text\" -f null -";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new Exception($"Transcription failed (exit code {process.ExitCode}): {stderr}");
        }

        if (!File.Exists(outputTextPath))
        {
            return "";
        }

        var text = await File.ReadAllTextAsync(outputTextPath);
        File.Delete(outputTextPath);
        return text.Trim();
    }
}
