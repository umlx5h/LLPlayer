using System.IO;
using System.Text;
using UtfUnknown;

namespace FlyleafLib;

#nullable enable

public class TextEncodings
{
    private static Encoding? DetectEncodingInternal(byte[] data)
    {
        // 1. Check Unicode BOM
        Encoding? encoding = DetectEncodingWithBOM(data);
        if (encoding != null)
        {
            return encoding;
        }

        // 2. If no BOM, then check text is UTF-8 without BOM
        // Perform UTF-8 check first because automatic detection often results in false positives such as WINDOWS-1252.
        if (IsUtf8(data))
        {
            return Encoding.UTF8;
        }

        // 3. Auto detect encoding using library
        try
        {
            var result = CharsetDetector.DetectFromBytes(data);
            return result.Detected.Encoding;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detect character encoding of text files
    /// </summary>
    /// <param name="path">file path</param>
    /// <param name="maxBytes">Bytes to read</param>
    /// <returns>Detected Encoding or null</returns>
    public static Encoding? DetectEncoding(string path, int maxBytes = 1 * 1024 * 1024)
    {
        byte[] data = new byte[maxBytes];

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
            int bytesRead = fs.Read(data, 0, data.Length);
            Array.Resize(ref data, bytesRead);
        }
        catch
        {
            return null;
        }

        return DetectEncodingInternal(data);
    }

    /// <summary>
    /// Detect character encoding using BOM
    /// </summary>
    /// <param name="bytes">string raw data</param>
    /// <returns>Detected Encoding or null</returns>
    private static Encoding? DetectEncodingWithBOM(byte[] bytes)
    {
        // UTF-8 BOM: EF BB BF
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        // UTF-16 LE BOM: FF FE
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode; // UTF-16 LE
        }

        // UTF-16 BE BOM: FE FF
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode; // UTF-16 BE
        }

        // UTF-32 LE BOM: FF FE 00 00
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.UTF32;
        }

        // UTF-32 BE BOM: 00 00 FE FF
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        // No BOM
        return null;
    }

    private static bool IsUtf8(byte[] bytes)
    {
        // enable validation
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        try
        {
            encoding.GetString(bytes);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
