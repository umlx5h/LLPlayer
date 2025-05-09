using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Color = System.Drawing.Color;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace FlyleafLib.MediaPlayer;

#nullable enable

public unsafe class SubtitlesOCR
{
    private readonly Config.SubtitlesConfig _config;
    private int _subNum;

    private readonly CancellationTokenSource?[] _ctss;
    private readonly object[] _lockers;
    private IOCRService? _ocrService ;

    public SubtitlesOCR(Config.SubtitlesConfig config, int subNum)
    {
        _config = config;
        _subNum = subNum;

        _lockers = new object[subNum];
        _ctss = new CancellationTokenSource[subNum];
        for (int i = 0; i < subNum; i++)
        {
            _lockers[i] = new object();
            _ctss[i] = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Try to initialize OCR Engine
    /// </summary>
    /// <param name="lang">OCR Language</param>
    /// <param name="err">expected initialize error</param>
    /// <returns>whether to success to initialize</returns>
    public bool TryInitialize(int subIndex, Language lang, out string err)
    {
        lang = GetLanguageWithFallback(subIndex, lang);

        // Retaining engines will increase memory usage, so they are created and discarded on the fly.
        IOCRService ocrService = _config[subIndex].OCREngine switch
        {
            SubOCREngineType.Tesseract => new TesseractOCRService(_config),
            SubOCREngineType.MicrosoftOCR => new MicrosoftOCRService(_config),
            _ => throw new InvalidOperationException(),
        };

        if (!ocrService.TryInitialize(lang, out err))
        {
            return false;
        }

        _ocrService = ocrService;

        err = "";
        return true;
    }

    /// <summary>
    /// Do OCR
    /// </summary>
    /// <param name="subIndex">0: Primary, 1: Secondary</param>
    /// <param name="subs">List of subtitle data for OCR</param>
    /// <param name="startTime">Timestamp to start OCR</param>
    public void Do(int subIndex, List<SubtitleData> subs, TimeSpan? startTime = null)
    {
        if (_ocrService == null)
            throw new InvalidOperationException("ocrService is not initialized. you must call TryInitialize() first");

        if (subs.Count == 0 || !subs[0].IsBitmap)
            return;

        // Cancel preceding OCR
        TryCancelWait(subIndex);

        lock (_lockers[subIndex])
        {
            // NOTE: important to dispose inside lock
            using IOCRService ocrService = _ocrService;

            _ctss[subIndex] = new CancellationTokenSource();

            int startIndex = 0;
            // Start OCR from the current playback point
            if (startTime.HasValue)
            {
                int match = subs.FindIndex(s => s.StartTime >= startTime);
                if (match != -1)
                {
                    // Do from 5 previous subtitles
                    startIndex = Math.Max(0, match - 5);
                }
            }

            for (int i = 0; i < subs.Count; i++)
            {
                if (_ctss[subIndex]!.Token.IsCancellationRequested)
                {
                    foreach (var sub in subs)
                    {
                        sub.Dispose();
                    }

                    break;
                }

                int index = (startIndex + i) % subs.Count;

                SubtitleBitmapData? bitmap = subs[index].Bitmap;
                if (bitmap == null)
                    continue;

                // TODO: L: If it's disposed, do I need to cancel it later?
                subs[index].Text = Process(ocrService, subIndex, bitmap);
                if (!string.IsNullOrEmpty(subs[index].Text))
                {
                    // If OCR succeeds, dispose of it (if it fails, leave it so that it can be displayed in the sidebar).
                    subs[index].Dispose();
                }
            }

            if (!_ctss[subIndex]!.Token.IsCancellationRequested)
            {
                // TODO: L: Notify, express completion in some way
                Utils.PlayCompletionSound();
            }
        }
    }

    private Language GetLanguageWithFallback(int subIndex, Language lang)
    {
        if (lang == Language.Unknown)
        {
            // fallback to user set language
            lang = subIndex == 0 ? _config.LanguageFallbackPrimary : _config.LanguageFallbackSecondary;
        }

        return lang;
    }

    public void TryCancelWait(int subIndex)
    {
        if (_ctss[subIndex] != null)
        {
            // Cancel if preceding OCR is running
            _ctss[subIndex]!.Cancel();

            // Wait until it is canceled by taking a lock
            lock (_lockers[subIndex])
            {
                _ctss[subIndex]?.Dispose();
                _ctss[subIndex] = null;
            }
        }
    }

    public static string Process(IOCRService ocrService, int subIndex, SubtitleBitmapData sub)
    {
        (byte[] data, AVSubtitleRect rect) = sub.SubToBitmap(true);

        int width = rect.w;
        int height = rect.h;

        fixed (byte* ptr = data)
        {
            // TODO: L: Make it possible to set true here in config (should the bitmap itself have an invert function automatically?)
            Binarize(width, height, ptr, 4, true);
        }

        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, bitmap.PixelFormat);
        Marshal.Copy(data, 0, bitmapData.Scan0, data.Length);
        bitmap.UnlockBits(bitmapData);

        // Perform preprocessing to improve accuracy before OCR (common processing independent of OCR method)
        using Bitmap ocrBitmap = Preprocess(bitmap);

        string ocrText = ocrService.RecognizeTextAsync(ocrBitmap).GetAwaiter().GetResult();
        string processedText = ocrService.PostProcess(ocrText);

        return processedText;
    }

    private static Bitmap Preprocess(Bitmap bitmap)
    {
        using Bitmap blackText = ImageProcessor.BlackText(bitmap);
        Bitmap padded = ImageProcessor.AddPadding(blackText, 20);

        return padded;
    }

    /// <summary>
    /// Perform binarization on bitmaps
    /// </summary>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <param name="buffer"></param>
    /// <param name="pixelByte"></param>
    /// <param name="srcTextWhite"></param>
    private static void Binarize(int width, int height, byte* buffer, int pixelByte, bool srcTextWhite)
    {
        // Black text on white background
        byte white = 255;
        byte black = 0;
        if (srcTextWhite)
        {
            // The text is white on a black background, so invert it to black text.
            white = 0;
            black = 255;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte* pixel = buffer + (y * width + x) * pixelByte;
                // Take out the first R bits since they are already in grayscale
                int grayscale = pixel[0];
                byte binaryValue = grayscale < 128 ? black : white;
                pixel[0] = pixel[1] = pixel[2] = binaryValue;
            }
        }
    }

    public void Reset(int subIndex)
    {
        TryCancelWait(subIndex);
    }
}

public interface IOCRService : IDisposable
{
    bool TryInitialize(Language lang, out string err);
    Task<string> RecognizeTextAsync(Bitmap bitmap);
    string PostProcess(string text);
}

public class TesseractOCRService : IOCRService
{
    private readonly Config.SubtitlesConfig _config;
    private TesseractOCR.Engine? _ocrEngine;
    private bool _disposed;
    private Language? _lang;

    public TesseractOCRService(Config.SubtitlesConfig config)
    {
        _config = config;
    }

    public bool TryInitialize(Language lang, out string err)
    {
        _lang = lang;

        string iso6391 = lang.ISO6391;

        if (iso6391 == "nb")
        {
            // Norwegian Bokmål to Norwegian
            iso6391 = "no";
        }

        Dictionary<string, List<TesseractModel>> tesseractModels = TesseractModelLoader.GetAvailableModels();

        if (!tesseractModels.TryGetValue(iso6391, out List<TesseractModel>? models))
        {
            err = $"Language:{lang.TopEnglishName} ({iso6391}) is not available in Tesseract OCR, Please download a model in settings if available language.";

            return false;
        }

        TesseractModel model = models.First();
        if (_config.TesseractOcrRegions != null && models.Count >= 2)
        {
            // choose zh-CN or zh-TW (for Chinese)
            if (_config.TesseractOcrRegions.TryGetValue(iso6391, out string? langCode))
            {
                TesseractModel? m = models.FirstOrDefault(m => m.LangCode == langCode);
                if (m != null)
                {
                    model = m;
                }
            }
        }

        _ocrEngine = new TesseractOCR.Engine(
            TesseractModel.ModelsDirectory,
            model.Lang);

        bool isCJK = model.Lang is
            TesseractOCR.Enums.Language.Japanese or
            TesseractOCR.Enums.Language.Korean or
            TesseractOCR.Enums.Language.ChineseSimplified or
            TesseractOCR.Enums.Language.ChineseTraditional;

        if (isCJK)
        {
            // remove whitespace around word if CJK
            _ocrEngine.SetVariable("preserve_interword_spaces", 1);
        }

        err = string.Empty;
        return true;
    }

    public Task<string> RecognizeTextAsync(Bitmap bitmap)
    {
        if (_ocrEngine == null)
            throw new InvalidOperationException("ocrEngine is not initialized");

        using MemoryStream stream = new();

        // 32bit -> 24bit conversion
        Bitmap converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(converted))
        {
            g.DrawImage(bitmap, 0, 0);
        }

        converted.Save(stream, ImageFormat.Bmp);

        stream.Position = 0;

        using var img = TesseractOCR.Pix.Image.LoadFromMemory(stream);

        using var page = _ocrEngine.Process(img);
        return Task.FromResult(page.Text);
    }

    public string PostProcess(string text)
    {
        var processedText = text;

        if (_lang == Language.English)
        {
            processedText = processedText
                .Replace("|", "I")
                .Replace("I1t's", "It's")
                .Replace("’'", "'");
        }

        // common
        processedText = processedText
            .Trim();

        return processedText;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _ocrEngine?.Dispose();
        _disposed = true;
    }
}

public class MicrosoftOCRService : IOCRService
{
    private readonly Config.SubtitlesConfig _config;
    private OcrEngine? _ocrEngine;
    private bool _isCJK;
    private bool _disposed;

    public MicrosoftOCRService(Config.SubtitlesConfig config)
    {
        _config = config;
    }

    public bool TryInitialize(Language lang, out string err)
    {
        string iso6391 = lang.ISO6391;

        string? langTag = null;

        if (_config.MsOcrRegions.TryGetValue(iso6391, out string? tag))
        {
            // If there is a preferred language region in the settings, it is given priority.
            langTag = tag;
        }
        else
        {
            var availableLangs = OcrEngine.AvailableRecognizerLanguages.ToList();

            // full match
            var match = availableLangs.FirstOrDefault(l => l.LanguageTag == iso6391);

            if (match == null)
            {
                // left match
                match = availableLangs.FirstOrDefault(l => l.LanguageTag.StartsWith($"{iso6391}-"));
            }

            if (match != null)
            {
                langTag = match.LanguageTag;
            }
        }

        if (langTag == null)
        {
            err = $"Language:{lang.TopEnglishName} ({iso6391}) is not available in Microsoft OCR, Please install an OCR engine if available language.";
            return false;
        }

        var language = new Windows.Globalization.Language(langTag);

        OcrEngine ocrEngine = OcrEngine.TryCreateFromLanguage(language);
        if (ocrEngine == null)
        {
            err = $"Language:{lang.TopEnglishName} ({iso6391}) is not available in Microsoft OCR (TryCreateFromLanguage), Please install an OCR engine if available language.";
            return false;
        }

        _ocrEngine = ocrEngine;
        _isCJK = langTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || // Chinese
                 langTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);   // Japanese

        err = string.Empty;
        return true;
    }

    public async Task<string> RecognizeTextAsync(Bitmap bitmap)
    {
        if (_ocrEngine == null)
            throw new InvalidOperationException("ocrEngine is not initialized");

        using MemoryStream ms = new();

        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        using IRandomAccessStream randomAccessStream = ms.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        OcrResult ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);

        if (_isCJK)
        {
            // remove whitespace around word if CJK
            return string.Join(Environment.NewLine,
                ocrResult.Lines.Select(line => string.Concat(line.Words.Select(word => word.Text))));
        }

        return string.Join(Environment.NewLine, ocrResult.Lines.Select(l => l.Text));
    }

    public string PostProcess(string text)
    {
        return text;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // do nothing
        _disposed = true;
    }
}

public static class ImageProcessor
{
    /// <summary>
    /// Converts to black text on a white background
    /// </summary>
    /// <param name="original">original bitmap</param>
    /// <returns>processed bitmap</returns>
    public static Bitmap BlackText(Bitmap original)
    {
        Bitmap converted = new(original.Width, original.Height, original.PixelFormat);

        using (Graphics g = Graphics.FromImage(converted))
        {
            // Convert to black text on a white background
            g.Clear(Color.White);

            // Drawing images with alpha blending enabled
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

            g.DrawImage(original, 0, 0);
        }

        return converted;
    }

    /// <summary>
    /// Add white padding around the bitmap
    /// </summary>
    /// <param name="original">original bitmap</param>
    /// <param name="padding">Size of padding to be added (in pixels)</param>
    /// <returns>padded bitmap</returns>
    public static Bitmap AddPadding(Bitmap original, int padding)
    {
        int newWidth = original.Width + padding * 2;
        int newHeight = original.Height + padding * 2;

        Bitmap paddedBitmap = new(newWidth, newHeight, original.PixelFormat);

        using (Graphics graphics = Graphics.FromImage(paddedBitmap))
        {
            // White background
            graphics.Clear(Color.White);

            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Draw the original image in the center
            graphics.DrawImage(original, padding, padding, original.Width, original.Height);
        }

        return paddedBitmap;
    }

    /// <summary>
    /// Enlarge the size of the bitmap while maintaining the aspect ratio.
    /// </summary>
    /// <param name="original">source bitmap</param>
    /// <returns>processed bitmap</returns>
    private static Bitmap ResizeBitmap(Bitmap original, double scaleFactor)
    {
        // Calculate new size
        int newWidth = (int)(original.Width * scaleFactor);
        int newHeight = (int)(original.Height * scaleFactor);

        Bitmap resizedBitmap = new(newWidth, newHeight);

        using (Graphics graphics = Graphics.FromImage(resizedBitmap))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // resize
            graphics.DrawImage(original, 0, 0, newWidth, newHeight);
        }

        return resizedBitmap;
    }
}
