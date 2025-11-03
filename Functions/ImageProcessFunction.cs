using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace WeatherImageApp.Functions
{
    public sealed class ImageProcessFunction
    {
        private readonly ILogger<ImageProcessFunction> _logger;
        private readonly BlobServiceClient _blobSvc;

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
                // 1. load background from assets
                using Image<Rgba32> image = LoadBackgroundImage();

                // 2. draw text
                var font = LoadFont(36f);
                var text = $"{station}: {temperature:F1}°C";

                image.Mutate(x =>
                {
                    x.DrawText(text, font, Color.White, new PointF(20, 20));
                });

                // 3. save to blob
                using var ms = new MemoryStream();
                await image.SaveAsJpegAsync(ms);
                ms.Position = 0;

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

        private static Image<Rgba32> LoadBackgroundImage()
        {
            // look in bin/.../assets/
            var bgPath = Path.Combine(AppContext.BaseDirectory, "assets", "weather-bg.jpg");

            if (File.Exists(bgPath))
            {
                return Image.Load<Rgba32>(bgPath);
            }

            // fallback: make simple blue image
            var img = new Image<Rgba32>(800, 600);
            img.Mutate(x => x.Fill(Color.SteelBlue));
            return img;
        }

        private static Font LoadFont(float size = 36f)
        {
            // 1. try asset font
            var fontPath = Path.Combine(AppContext.BaseDirectory, "assets", "OpenSans-Regular.ttf");
            if (File.Exists(fontPath))
            {
                var collection = new FontCollection();
                var family = collection.Add(fontPath);
                return family.CreateFont(size);
            }

            // 2. fallback to any system font
            var families = SystemFonts.Collection.Families.ToArray();
            if (families.Length == 0)
            {
                throw new Exception("No fonts available (asset not found and no system fonts).");
            }

            return families[0].CreateFont(size);
        }
    }
}
