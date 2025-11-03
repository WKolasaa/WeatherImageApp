using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WeatherImageApp.Helpers;
using WeatherImageApp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.Fonts;

namespace WeatherImageApp.Functions
{
    public class ImageProcessFunction
    {
        private readonly ILogger _logger;
        private readonly string _storageConnection;
        private readonly string _imagesContainer;
        private readonly string _jobStatusTable;

        public ImageProcessFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<ImageProcessFunction>();

            _storageConnection = ConfigHelper.Get(
                "Storage:ConnectionString",
                ConfigHelper.Get("AzureWebJobsStorage")
            );

            _imagesContainer = ConfigHelper.Get(
                "Storage:ImagesContainerName",
                ConfigHelper.Get("IMAGE_OUTPUT_CONTAINER", "images")
            );

            _jobStatusTable = ConfigHelper.Get("Storage:JobStatusTableName", "JobStatus");
        }

        [Function("ImageProcessFunction")]
        public async Task Run(
            [QueueTrigger("%Storage:ImageQueueName%", Connection = "AzureWebJobsStorage")]
            string queueMessage)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(queueMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot parse queue message: {Message}", queueMessage);
                return;
            }

            string jobId = doc.RootElement.GetProperty("JobId").GetString()!;
            string stationName = doc.RootElement.GetProperty("StationName").GetString()!;
            double? temperature = doc.RootElement.TryGetProperty("Temperature", out var t)
                                  && t.ValueKind == JsonValueKind.Number
                ? t.GetDouble()
                : (double?)null;

            _logger.LogInformation("Generating image for {Station} ({Temp}°C) for job {JobId}",
                stationName, temperature, jobId);

            byte[] imageBytes = await GetBaseImageBytesAsync();
            if (imageBytes.Length == 0)
            {
                _logger.LogWarning("Base image not found at ./assets/base-weather.jpg");
                return;
            }

            using var image = Image.Load(imageBytes);

            var fontPath = Path.Combine(Environment.CurrentDirectory, "assets", "OpenSens-Regular.ttf");
            Font font;
            if (File.Exists(fontPath))
            {
                var collection = new FontCollection();
                var family = collection.Add(fontPath);
                font = family.CreateFont(36, FontStyle.Bold);
            }
            else
            {
                _logger.LogWarning("Font file not found at {FontPath}; text may not render.", fontPath);
                var collection = new FontCollection();
                font = SystemFonts.CreateFont("Arial", 36); 
            }

            var text = temperature.HasValue
                ? $"{stationName}: {temperature.Value:F1}°C"
                : stationName;

            image.Mutate(ctx =>
            {
                ctx.DrawText(
                    text,
                    font,
                    SixLabors.ImageSharp.Color.White,
                    new PointF(20, 40));
            });

            var blobService = new BlobServiceClient(_storageConnection);
            var container = blobService.GetBlobContainerClient(_imagesContainer);
            await container.CreateIfNotExistsAsync();

            var safeStation = stationName
                .Replace(' ', '_')
                .Replace("/", "_")
                .Replace("\\", "_");

            var blobName = $"{jobId}/{safeStation}.jpg";
            var blobClient = container.GetBlobClient(blobName);

            using (var outStream = new MemoryStream())
            {
                await image.SaveAsync(outStream, new JpegEncoder());
                outStream.Position = 0;
                await blobClient.UploadAsync(outStream, overwrite: true);
            }

            _logger.LogInformation("Uploaded blob {BlobName} to container {Container}",
                blobName, _imagesContainer);

            var tableService = new TableServiceClient(_storageConnection);
            var tableClient = tableService.GetTableClient(_jobStatusTable);
            await tableClient.CreateIfNotExistsAsync();

            await UpdateJobStatusWithRetryAsync(tableClient, jobId);
        }

        private async Task UpdateJobStatusWithRetryAsync(TableClient tableClient, string jobId)
        {
            const string partitionKey = "jobs";
            const int maxRetries = 5;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var resp = await tableClient.GetEntityAsync<JobStatusEntity>(partitionKey, jobId);
                    var entity = resp.Value;

                    entity.ProcessedStations += 1;
                    entity.LastUpdatedUtc = DateTimeOffset.UtcNow;

                    if (entity.TotalStations > 0 && entity.ProcessedStations >= entity.TotalStations)
                    {
                        entity.Status = "Completed";
                    }
                    else if (string.IsNullOrWhiteSpace(entity.Status))
                    {
                        entity.Status = "Processing";
                    }

                    await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);

                    _logger.LogInformation("Updated jobstatus for {JobId}: {Status} ({Processed}/{Total})",
                        jobId, entity.Status, entity.ProcessedStations, entity.TotalStations);

                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    var newEntity = new TableEntity(partitionKey, jobId)
                    {
                        ["Status"] = "Processing",
                        ["ProcessedStations"] = 1,
                        ["TotalStations"] = 1,
                        ["LastUpdatedUtc"] = DateTimeOffset.UtcNow
                    };

                    await tableClient.AddEntityAsync(newEntity);
                    _logger.LogInformation("Created new jobstatus row for {JobId}", jobId);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    _logger.LogWarning("Concurrency conflict updating job {JobId}, attempt {Attempt}/{Max}",
                        jobId, attempt, maxRetries);

                    await Task.Delay(50 * attempt);
                    continue;
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError(ex, "Failed to update jobstatus for {JobId}", jobId);
                    return;
                }
            }

            _logger.LogError("Failed to update jobstatus for {JobId} after {Max} attempts",
                jobId, maxRetries);
        }

        private async Task<byte[]> GetBaseImageBytesAsync()
        {
            var localPath = "./assets/base-weather.jpg";

            if (File.Exists(localPath))
            {
                return await File.ReadAllBytesAsync(localPath);
            }

            return Array.Empty<byte>();
        }
    }
}
