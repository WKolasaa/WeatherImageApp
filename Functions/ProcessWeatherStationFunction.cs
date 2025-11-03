using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WeatherImageApp.Helpers;
using WeatherImageApp.Models;

namespace WeatherImageApp.Functions
{
    public class ProcessWeatherStationFunction
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly string _storageConnection;

        public ProcessWeatherStationFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<ProcessWeatherStationFunction>();
            _httpClient = new HttpClient();
            _storageConnection = ConfigHelper.Get("AzureWebJobsStorage");
        }

        [Function("ProcessWeatherStationFunction")]
        public async Task Run([QueueTrigger("%Storage:StartQueueName%", Connection = "AzureWebJobsStorage")] string queueMessage)
        {
            var msg = JsonSerializer.Deserialize<WeatherJobMessage>(queueMessage);
            if (msg == null || string.IsNullOrEmpty(msg.JobId))
            {
                _logger.LogError("Invalid start-jobs message: {Message}", queueMessage);
                return;
            }

            string jobId = msg.JobId;
            _logger.LogInformation("🌦️ Processing weather data for job {JobId}", jobId);

            var buienUrl = ConfigHelper.Get("Api:BuienradarUrl", "https://data.buienradar.nl/2.0/feed/json");

            var response = await _httpClient.GetAsync(buienUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var stations = new List<JsonElement>();
            if (doc.RootElement.TryGetProperty("actual", out var actual) &&
                actual.TryGetProperty("stationmeasurements", out var list))
            {
                foreach (var s in list.EnumerateArray())
                    stations.Add(s);
            }

            var maxStations = ConfigHelper.GetInt("Api:StationsToProcess", 0);
            if (maxStations > 0 && stations.Count > maxStations)
                stations = stations.GetRange(0, maxStations);

            var imageQueueName = ConfigHelper.Get("Storage:ImageQueueName", "image-jobs");
            var imageQueue = new QueueClient(_storageConnection, imageQueueName);
            await imageQueue.CreateIfNotExistsAsync();

            int counter = 0;
            foreach (var station in stations)
            {
                counter++;
                var stationName = station.GetProperty("stationname").GetString() ?? $"Station {counter}";
                var temp = station.TryGetProperty("temperature", out var t) ? t.GetDouble() : (double?)null;

                var imageMsg = new
                {
                    JobId = jobId,
                    StationName = stationName,
                    Temperature = temp
                };

                var payload = JsonSerializer.Serialize(imageMsg);
                await imageQueue.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)));

                _logger.LogInformation("Queued image job #{Count} for {Station}", counter, stationName);
            }

            // update table
            var tableName = ConfigHelper.Get("Storage:JobStatusTableName", "JobStatus");
            var tableService = new TableServiceClient(_storageConnection);
            var tableClient = tableService.GetTableClient(tableName);
            await tableClient.CreateIfNotExistsAsync();

            try
            {
                var job = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId);
                var entity = job.Value;
                entity.TotalStations = counter;
                entity.Status = "Processing";
                entity.LastUpdatedUtc = DateTimeOffset.UtcNow;
                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update jobstatus for {JobId}", jobId);
            }

            _logger.LogInformation("✅ Queued {Count} stations for job {JobId}", counter, jobId);
        }
    }
}
