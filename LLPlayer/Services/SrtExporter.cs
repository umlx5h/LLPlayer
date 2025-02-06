using System.IO;
using System.Text;

namespace LLPlayer.Services;

public static class SrtExporter
{
    // TODO: L: Supports tags such as <i>?
    public static void ExportSrt(List<SubtitleLine> lines, string filePath, Encoding encoding)
    {
        using StreamWriter writer = new(filePath, false, encoding);

        foreach (var (i, line) in lines.Index())
        {
            writer.WriteLine((i + 1).ToString());
            writer.WriteLine($"{FormatTime(line.Start)} --> {FormatTime(line.End)}");
            writer.WriteLine(line.Text);
            // blank line expect last
            if (i != lines.Count - 1)
            {
                writer.WriteLine();
            }
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return string.Format("{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours,
            time.Minutes,
            time.Seconds,
            time.Milliseconds);
    }
}

public class SubtitleLine
{
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required string Text { get; init; }
}
