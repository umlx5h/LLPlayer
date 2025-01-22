using System.IO;
using static FlyleafLib.Utils;

namespace LLPlayer.Extensions;

public static class FileHelper
{
    /// <summary>
    /// Retrieves the next and previous file from the specified file path.
    /// Select files with the same extension in the same folder, sorted in natural alphabetical order.
    /// Returns null if the next or previous file does not exist.
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static (string? prev, string? next) GetNextAndPreviousFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("file does not exist", filePath);
        }

        string? directory = Path.GetDirectoryName(filePath);
        string? extension = Path.GetExtension(filePath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(extension))
        {
            throw new InvalidOperationException($"filePath is invalid: {filePath}");
        }

        // Get files with the same extension, ignoring case
        List<string> foundFiles = Directory.GetFiles(directory, $"*{extension}")
                                  .Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(f => Path.GetFileName(f), new NaturalStringComparer())
                                  .ToList();

        if (foundFiles.Count == 0)
        {
            throw new InvalidOperationException($"same extension file does not exist: {filePath}");
        }

        int currentIndex = foundFiles.FindIndex(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
        if (currentIndex == -1)
        {
            throw new InvalidOperationException($"current file does not exist: {filePath}");
        }

        string? next = (currentIndex < foundFiles.Count - 1) ? foundFiles[currentIndex + 1] : null;
        string? prev = (currentIndex > 0) ? foundFiles[currentIndex - 1] : null;

        return (prev, next);
    }
}
