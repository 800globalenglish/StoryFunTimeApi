using System.Diagnostics;

namespace StoryFunTimeApi.Services;

public class VideoService
{
    // Runs an ffmpeg command and waits for it to finish, throwing if it fails.
    private async Task RunFfmpeg(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new Exception($"ffmpeg failed (exit code {process.ExitCode}): {stderr}");
        }
    }

    /// <summary>
    /// Builds a single video from a book's pages, each shown for the length of its
    /// own narration audio. Returns the final video's path on disk.
    /// </summary>
    public async Task<string> GenerateBookVideo(
        List<(int PageNumber, string ImagePath, string AudioPath)> pages,
        string outputDir,
        string bookId)
    {
        Directory.CreateDirectory(outputDir);
        var tempDir = Path.Combine(outputDir, $"temp_{bookId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var clipPaths = new List<string>();

            // Step 1: turn each page into its own short clip (image held for the length of its audio)
            foreach (var page in pages.OrderBy(p => p.PageNumber))
            {
                var clipPath = Path.Combine(tempDir, $"page_{page.PageNumber}.mp4");
                var args = $"-y -loop 1 -i \"{page.ImagePath}\" -i \"{page.AudioPath}\" " +
                           $"-c:v libx264 -tune stillimage -c:a aac -b:a 192k -pix_fmt yuv420p " +
                           $"-vf \"scale=1280:-2\" -shortest \"{clipPath}\"";
                await RunFfmpeg(args);
                clipPaths.Add(clipPath);
            }

            // Step 2: join all the page clips into one final video
            var listFilePath = Path.Combine(tempDir, "filelist.txt");
            var listContent = string.Join("\n", clipPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'"));
            await File.WriteAllTextAsync(listFilePath, listContent);

            var finalFileName = $"{bookId}.mp4";
            var finalPath = Path.Combine(outputDir, finalFileName);
            var concatArgs = $"-y -f concat -safe 0 -i \"{listFilePath}\" -c copy \"{finalPath}\"";
            await RunFfmpeg(concatArgs);

            return finalPath;
        }
        finally
        {
            // Clean up the intermediate per-page clips, keep only the final video
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
