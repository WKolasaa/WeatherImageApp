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

namespace WeatherImageApp.Functions
{
    public class ImageProcessFunction
    {
        private readonly ILogger _logger;
        private readonly string _storageConnection;

        public ImageProcessFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<ImageProcessFunction>();
            _storageConnection = ConfigHelper.Get("AzureWebJobsStorage");
        }

        [Function("ImageProcessFunction")]
        public async Task Run([QueueTrigger("%Storage:ImageQueueName%", Connection = "AzureWebJobsStorage")] string queueMessage)
        {
            var doc = JsonDocument.Parse(queueMessage);
            string jobId = doc.RootElement.GetProperty("JobId").GetString()!;
            string stationName = doc.RootElement.GetProperty("StationName").GetString()!;
            double? temperature = doc.RootElement.TryGetProperty("Temperature", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetDouble()
                : (double?)null;

            _logger.LogInformation("🖼️ Generating image for {Station} ({Temp}°C)", stationName, temperature);

            byte[] imageBytes = await GetBaseImageBytesAsync();

            var containerName = ConfigHelper.Get("Storage:ImagesContainerName",
                                 ConfigHelper.Get("IMAGE_OUTPUT_CONTAINER", "images"));
            var blobService = new BlobServiceClient(_storageConnection);
            var container = blobService.GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync();

            var safeStation = stationName.Replace(' ', '_').Replace("/", "_").Replace("\\", "_");
            var blobName = $"{jobId}/{safeStation}.jpg";
            var blobClient = container.GetBlobClient(blobName);

            using (var ms = new MemoryStream(imageBytes))
            {
                await blobClient.UploadAsync(ms, overwrite: true);
            }

            _logger.LogInformation("📦 Uploaded {BlobName}", blobName);

            // update table
            var tableName = ConfigHelper.Get("Storage:JobStatusTableName", "JobStatus");
            var tableService = new TableServiceClient(_storageConnection);
            var tableClient = tableService.GetTableClient(tableName);
            await tableClient.CreateIfNotExistsAsync();

            try
            {
                var job = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId);
                var entity = job.Value;
                entity.ProcessedStations += 1;
                entity.LastUpdatedUtc = DateTimeOffset.UtcNow;

                if (entity.TotalStations > 0 && entity.ProcessedStations >= entity.TotalStations)
                {
                    entity.Status = "Completed";
                }

                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Could not update jobstatus for {JobId}", jobId);
            }
        }

        private async Task<byte[]> GetBaseImageBytesAsync()
        {
            var localPath = "/mnt/c/Users/wojtu/Desktop/Inholland/Year 4/WeatherImageApp/bin/output/assets/base-weather.jpg";
            if (File.Exists(localPath))
            {
                return await File.ReadAllBytesAsync(localPath);
            }
            return Array.Empty<byte>();
        }
    }
}
