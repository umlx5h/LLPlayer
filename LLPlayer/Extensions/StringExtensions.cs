using System.IO;

namespace LLPlayer.Extensions;

public static class StringExtensions
{
    /// <summary>
    /// Split for various types of newline codes
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static IEnumerable<string> SplitToLines(this string? input)
    {
        if (input == null)
        {
            yield break;
        }

        using StringReader reader = new(input);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }
}
