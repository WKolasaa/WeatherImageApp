using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using System.Text.Json;

namespace WeatherImageApp.Functions
{
    public class GetResultsFunction
    {
        private readonly ILogger<GetResultsFunction> _logger;
        public GetResultsFunction(ILogger<GetResultsFunction> logger) => _logger = logger;

        [Function("GetResultsFunction")]
        public async Task<HttpResponseData> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "results/{jobId}")] HttpRequestData req,
            string jobId)
        {
            var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? "UseDevelopmentStorage=true";
            var blobService = new BlobServiceClient(conn);
            var container = blobService.GetBlobContainerClient("images");
            await container.CreateIfNotExistsAsync();

            var uris = new List<string>();
            await foreach (var blob in container.GetBlobsAsync())
            {
                if (blob.Name.StartsWith(jobId, StringComparison.OrdinalIgnoreCase))
                    uris.Add($"{container.Uri}/{blob.Name}");
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteStringAsync(JsonSerializer.Serialize(new { jobId, images = uris }));
            return res;
        }
    }
}
