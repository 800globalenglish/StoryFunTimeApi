using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StoryFunTimeApi.Services;

public class TranscriptSegment
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = "";
}

public class RecordingPage
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = "";
}

public class TranscriptionService
{
    private readonly string _modelPath;

    public TranscriptionService(IConfiguration configuration)
    {
        _modelPath = configuration["Whisper:ModelPath"] ?? "whisper-models/ggml-base.en.bin";
    }

    // EXISTING - unchanged. Still used for per-page recording transcription.
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

    // NEW - same idea as Transcribe(), but asks Whisper for timestamped segments (SRT
    // format) instead of one plain-text blob. The timestamps are what let us cut the
    // original audio apart later, matching each page's text to its own audio slice.
    public async Task<List<TranscriptSegment>> TranscribeWithTimestamps(string audioFilePath)
    {
        var tempDir = "temp_transcripts";
        Directory.CreateDirectory(tempDir);
        var outputSrtPath = $"{tempDir}/transcript_{Guid.NewGuid()}.srt";

        var args = $"-i \"{audioFilePath}\" -vn -af \"whisper=model={_modelPath}:language=en:queue=3:destination={outputSrtPath}:format=srt\" -f null -";

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

        if (!File.Exists(outputSrtPath))
        {
            return new List<TranscriptSegment>();
        }

        var srtText = await File.ReadAllTextAsync(outputSrtPath);
        File.Delete(outputSrtPath);
        return ParseSrt(srtText);
    }

    // Parses standard SRT blocks, e.g.:
    // 1
    // 00:00:01,200 --> 00:00:04,800
    // Some transcribed text here
    private static List<TranscriptSegment> ParseSrt(string srtText)
    {
        var segments = new List<TranscriptSegment>();
        var blocks = Regex.Split(srtText.Trim(), @"\r?\n\r?\n");

        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2) continue;

            var timeLineIndex = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timeLineIndex == -1) continue;

            var times = lines[timeLineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (times.Length != 2) continue;

            if (!TryParseSrtTime(times[0], out var start)) continue;
            if (!TryParseSrtTime(times[1], out var end)) continue;

            var text = string.Join(" ", lines.Skip(timeLineIndex + 1)).Trim();
            if (text.Length == 0) continue;

            segments.Add(new TranscriptSegment { Start = start, End = end, Text = text });
        }

        return segments;
    }

    private static bool TryParseSrtTime(string s, out TimeSpan result)
    {
        // SRT format: 00:00:01,200  (comma as the decimal separator)
        s = s.Trim().Replace(',', '.');
        return TimeSpan.TryParseExact(s, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out result);
    }

    // NEW - groups timestamped segments into page-sized chunks by word count.
    // Never splits inside a Whisper segment (so every audio cut lands on a natural
    // pause), and respects a min/max page count so a short or long recording doesn't
    // produce too few or too many pages.
    public List<RecordingPage> GroupIntoPages(
        List<TranscriptSegment> segments,
        int targetWordsPerPage = 22,
        int minPages = 5,
        int maxPages = 20)
    {
        if (segments.Count == 0) return new List<RecordingPage>();

        int totalWords = segments.Sum(s => WordCount(s.Text));
        int idealPages = Math.Max(minPages, Math.Min(maxPages,
            (int)Math.Round((double)totalWords / targetWordsPerPage)));
        idealPages = Math.Min(idealPages, segments.Count); // can't have more pages than segments
        idealPages = Math.Max(idealPages, 1);

        int wordsPerPage = Math.Max(1, totalWords / idealPages);

        var pages = new List<RecordingPage>();
        var current = new List<TranscriptSegment>();
        int currentWords = 0;

        foreach (var seg in segments)
        {
            current.Add(seg);
            currentWords += WordCount(seg.Text);

            bool isBuildingLastPage = pages.Count == idealPages - 1;
            if (!isBuildingLastPage && currentWords >= wordsPerPage)
            {
                pages.Add(BuildPage(current));
                current = new List<TranscriptSegment>();
                currentWords = 0;
            }
        }

        if (current.Count > 0)
        {
            pages.Add(BuildPage(current));
        }

        return pages;
    }

    private static RecordingPage BuildPage(List<TranscriptSegment> segs) => new RecordingPage
    {
        Start = segs.First().Start,
        End = segs.Last().End,
        Text = string.Join(" ", segs.Select(s => s.Text.Trim()))
    };

    private static int WordCount(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // NEW - decodes the original recording into an uncompressed WAV file first.
    // Browser-recorded webm/opus files often lack a reliable time-index, so seeking
    // directly inside them for -ss/-t cuts can land in the wrong spot and produce
    // near-empty clips (this was the cause of the 1KB output files). Decoding once
    // to WAV gives FFmpeg something it can seek inside accurately, and every page's
    // slice gets cut from that instead of from the original file.
    public async Task<string> DecodeToWav(string sourceAudioPath)
    {
        var tempDir = "temp_recordings";
        Directory.CreateDirectory(tempDir);
        var wavPath = $"{tempDir}/decoded_{Guid.NewGuid()}.wav";

        var args = $"-i \"{sourceAudioPath}\" -ar 48000 -ac 1 -y \"{wavPath}\"";

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
            throw new Exception($"WAV decode failed (exit code {process.ExitCode}): {stderr}");
        }

        return wavPath;
    }

    // NEW - cuts a slice out of the original recording for one page, given its
    // start/end timestamps. Re-encodes to webm/opus to match the .webm files your
    // /pages/{id}/audio endpoint already saves, so the Flutter player handles these
    // identically to a normally-recorded page.
    public async Task<string> CutAudioSegment(string sourceAudioPath, TimeSpan start, TimeSpan end, string outputPath)
    {
        var duration = end - start;
        var args = $"-i \"{sourceAudioPath}\" -ss {start.TotalSeconds.ToString(CultureInfo.InvariantCulture)} " +
                    $"-t {duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)} " +
                    $"-c:a libopus -f webm -y \"{outputPath}\"";

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
            throw new Exception($"Audio cut failed (exit code {process.ExitCode}): {stderr}");
        }

        return outputPath;
    }
}