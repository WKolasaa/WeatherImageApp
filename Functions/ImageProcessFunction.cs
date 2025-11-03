using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;

namespace WeatherImageApp.Functions
{
    public sealed class ImageProcessFunction
    {
        private readonly ILogger<ImageProcessFunction> _logger;
        private readonly BlobServiceClient _blobSvc;

        // reuse 1 HttpClient for functions
        private static readonly HttpClient _http = new HttpClient();

        public ImageProcessFunction(ILogger<ImageProcessFunction> logger, BlobServiceClient blobSvc)
        {
            _logger = logger;
            _blobSvc = blobSvc;
        }

        [Function("ImageProcessFunction")]
        public async Task RunAsync(
            [QueueTrigger("image-process3", Connection = "AzureWebJobsStorage")] string payload)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(payload);
            var jobId = json.TryGetProperty("jobId", out var jobIdProp)
                ? jobIdProp.GetString() ?? "unknown"
                : "unknown";
            var station = json.TryGetProperty("station", out var stationProp)
                ? stationProp.GetString() ?? "unknown"
                : "unknown";
            var temperature = json.TryGetProperty("temperature", out var tempProp)
                ? tempProp.GetDouble()
                : 0.0;

            _logger.LogInformation("🖼️ Generating image for {Station} ({Temp}°C)", station, temperature);

            try
            {
                // 1. get base image bytes (public API -> asset -> generated)
                var imgBytes = await GetBaseImageBytesAsync(_logger);

                // 2. load into ImageSharp
                using var image = Image.Load<Rgba32>(imgBytes);

                // 3. draw text
                image.Mutate(x =>
                {
                    Font font;
                        try
                        {
                            font = SystemFonts.CreateFont("Arial", 36);
                        }
                        catch (SixLabors.Fonts.FontFamilyNotFoundException)
                        {
                            // fallback for Linux
                            font = SystemFonts.CreateFont("DejaVu Sans", 36);
                        }
                    var text = $"{station}: {temperature:F1}°C";

                    x.DrawText(text, font, Color.White, new PointF(20, 20));
                });

                // 4. save to stream
                using var ms = new MemoryStream();
                await image.SaveAsJpegAsync(ms);
                ms.Position = 0;

                // 5. upload to blob
                var container = _blobSvc.GetBlobContainerClient("images");
                await container.CreateIfNotExistsAsync();

                var safeStation = station.Replace(" ", "_");
                var blobName = $"{jobId}/{safeStation}.jpg";
                var blob = container.GetBlobClient(blobName);
                await blob.UploadAsync(ms, overwrite: true);

                _logger.LogInformation("📦 Uploaded {BlobName}", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error generating image for station {Station}", station);
            }
        }

        /// <summary>
        /// Tries, in order:
        /// 1) Unsplash public API
        /// 2) local assets/base-weather.jpg
        /// 3) generated blue image
        /// </summary>
        private static async Task<byte[]> GetBaseImageBytesAsync(ILogger log)
        {
            // 1) try public image API (to satisfy assignment)
            try
            {
                var unsplashUrl = "https://source.unsplash.com/random/800x600/?landscape,weather";
                var bytes = await _http.GetByteArrayAsync(unsplashUrl);
                log.LogInformation("🌄 Got base image from Unsplash");
                return bytes;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Unsplash unavailable. Falling back to local asset.");
            }

            // 2) try local asset
            try
            {
                var baseDir = Environment.CurrentDirectory;
                var assetPath = Path.Combine(baseDir, "assets", "base-weather.jpg");

                if (File.Exists(assetPath))
                {
                    log.LogInformation("🗂️ Using local asset image at {Path}", assetPath);
                    return await File.ReadAllBytesAsync(assetPath);
                }
                else
                {
                    log.LogWarning("Local asset image not found at {Path}. Will generate a solid image.", assetPath);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Error reading local asset image. Will generate a solid image.");
            }

            // 3) final fallback: generate solid image
            using var fallbackImage = new Image<Rgba32>(800, 600);
            fallbackImage.Mutate(x => x.Fill(Color.SteelBlue));
            using var ms = new MemoryStream();
            await fallbackImage.SaveAsJpegAsync(ms);
            return ms.ToArray();
        }
    }
}
