using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Fonts;

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
                using var http = new HttpClient();
                byte[] imgBytes;

                try
                {
                   
                    imgBytes = await http.GetByteArrayAsync("https://source.unsplash.com/random/800x600/?landscape,weather");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(" Unsplash unavailable ({Message}). Using fallback image.", ex.Message);

                    // 🔹 Create a fallback blue background image
                    using var fallbackImage = new Image<Rgba32>(800, 600);
                    fallbackImage.Mutate(x =>
                    {
                        x.Fill(Color.SteelBlue);
                        x.DrawText($"{station}: {temperature:F1}°C",
                            SystemFonts.CreateFont("Arial", 36),
                            Color.White,
                            new PointF(20, 20));
                    });

                    using var fallbackStream = new MemoryStream();
                    await fallbackImage.SaveAsJpegAsync(fallbackStream);
                    imgBytes = fallbackStream.ToArray();
                }

                using var image = Image.Load<Rgba32>(imgBytes);
                image.Mutate(x =>
                {
                    x.DrawText($"{station}: {temperature:F1}°C",
                        SystemFonts.CreateFont("Arial", 36),
                        Color.White,
                        new PointF(20, 20));
                });

                // 🔹 Save to blob storage
                using var ms = new MemoryStream();
                await image.SaveAsJpegAsync(ms);
                ms.Position = 0;

                var container = _blobSvc.GetBlobContainerClient("images");
                await container.CreateIfNotExistsAsync();

                var blobName = $"{jobId}/{station.Replace(" ", "_")}.jpg";
                var blob = container.GetBlobClient(blobName);
                await blob.UploadAsync(ms, overwrite: true);

                _logger.LogInformation(" Uploaded {BlobName}", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " Error generating image for station {Station}", station);
            }
        }
    }
}
