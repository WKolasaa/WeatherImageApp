using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class ProcessWeatherStationFunction
    {
        private readonly ILogger<ProcessWeatherStationFunction> _logger;
        private readonly QueueServiceClient _queueSvc;

        public ProcessWeatherStationFunction(ILogger<ProcessWeatherStationFunction> logger, QueueServiceClient queueSvc)
        {
            _logger = logger;
            _queueSvc = queueSvc;
        }

        [Function("ProcessWeatherStationFunction")]
        public async Task RunAsync([QueueTrigger("weather-jobs", Connection = "AzureWebJobsStorage")] string jobId)
        {
            _logger.LogInformation("FUNCTION STARTED - Processing weather data for job {JobId}...", jobId);

            try
            {
                _logger.LogInformation("Step 1: Creating HTTP client");
                using var http = new HttpClient();
                
                _logger.LogInformation("Step 2: Calling Buienradar API");
                var json = await http.GetStringAsync("https://data.buienradar.nl/2.0/feed/json");
                _logger.LogInformation("Step 3: Received {Length} bytes", json.Length);

                _logger.LogInformation("Step 4: Parsing JSON");
                using var doc = JsonDocument.Parse(json);
                
                if (!doc.RootElement.TryGetProperty("actual", out var actual) ||
                    !actual.TryGetProperty("stationmeasurements", out var stations))
                {
                    _logger.LogWarning("Buienradar response missing 'actual.stationmeasurements'");
                    return;
                }

                _logger.LogInformation("Step 5: Getting image queue");
                var imageQueue = _queueSvc.GetQueueClient("image-process3");
                await imageQueue.CreateIfNotExistsAsync();

                _logger.LogInformation("Step 6: Processing stations");
                var count = 0;
                foreach (var station in stations.EnumerateArray())
                {
                    if (!station.TryGetProperty("stationname", out var nameProp) ||
                        !station.TryGetProperty("temperature", out var tempProp))
                        continue;

                    var name = nameProp.GetString() ?? "unknown";
                    var temp = tempProp.GetDouble();

                    var payload = JsonSerializer.Serialize(new
                    {
                        jobId,
                        station = name,
                        temperature = temp
                    });

                    await imageQueue.SendMessageAsync(
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                    );
                    count++;
                    _logger.LogInformation("Queued image job #{Count} for {Station}", count, name);
                }
                
                _logger.LogInformation("FUNCTION COMPLETED - Queued {Count} stations", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing weather data for job {JobId}", jobId);
                throw; // Re-throw to see the actual error
            }
        }
    }
}