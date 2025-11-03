using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using WeatherImageApp.Helpers;
using WeatherImageApp.Models;

namespace WeatherImageApp.Functions
{
    public class StartJobFunction
    {
        private readonly ILogger _logger;
        private readonly string _storageConnection;

        public StartJobFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<StartJobFunction>();
            _storageConnection = ConfigHelper.Get("AzureWebJobsStorage");
            if (string.IsNullOrEmpty(_storageConnection))
                throw new InvalidOperationException("AzureWebJobsStorage not set");
        }

        [Function("StartJobFunction")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", Route = "jobs/start")] HttpRequestData req)
        {
            var expectedApiKey = ConfigHelper.Get("API_KEY");
            if (!string.IsNullOrEmpty(expectedApiKey))
            {
                req.Headers.TryGetValues("x-api-key", out var values);
                var given = values is null ? null : System.Linq.Enumerable.FirstOrDefault(values);
                if (!string.Equals(expectedApiKey, given, StringComparison.Ordinal))
                {
                    var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorized.WriteStringAsync("Invalid API key.");
                    return unauthorized;
                }
            }

            var jobId = Guid.NewGuid().ToString();
            _logger.LogInformation("Starting new job {JobId}", jobId);

            var tableName = ConfigHelper.Get("Storage:JobStatusTableName", "JobStatus");
            var tableService = new TableServiceClient(_storageConnection);
            var tableClient = tableService.GetTableClient(tableName);
            await tableClient.CreateIfNotExistsAsync();

            var entity = new JobStatusEntity
            {
                RowKey = jobId,
                Status = "Created",
                TotalStations = 0,
                ProcessedStations = 0,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
            await tableClient.UpsertEntityAsync(entity);

            var queueName = ConfigHelper.Get("Storage:StartQueueName", "start-jobs");
            var queueClient = new QueueClient(_storageConnection, queueName);
            await queueClient.CreateIfNotExistsAsync();

            var payload = JsonSerializer.Serialize(new WeatherJobMessage { JobId = jobId });
            await queueClient.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)));

            var res = req.CreateResponse(HttpStatusCode.OK);
            res.AddCors();
            await res.WriteAsJsonAsync(new { jobId });
            return res;
        }
    }
}
