using System.Net;
using Azure.Storage.Blobs;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class HealthFunction
    {
        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly TableServiceClient _tableServiceClient;
        private readonly IConfiguration _config;

        public HealthFunction(
            ILoggerFactory loggerFactory,
            BlobServiceClient blobServiceClient,
            TableServiceClient tableServiceClient,
            IConfiguration config)
        {
            _logger = loggerFactory.CreateLogger<HealthFunction>();
            _blobServiceClient = blobServiceClient;
            _tableServiceClient = tableServiceClient;
            _config = config;
        }

        [Function("Health")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        {
            var res = req.CreateResponse(HttpStatusCode.OK);

            var imagesContainerName = _config["Storage:ImagesContainerName"] ?? "images";
            var outputContainerName = _config["IMAGE_OUTPUT_CONTAINER"] ?? "output";
            var jobStatusTableName = _config["Storage:JobStatusTableName"] ?? "JobStatus";

            var imagesContainer = _blobServiceClient.GetBlobContainerClient(imagesContainerName);
            await imagesContainer.CreateIfNotExistsAsync();

            var outputContainer = _blobServiceClient.GetBlobContainerClient(outputContainerName);
            await outputContainer.CreateIfNotExistsAsync();

            var tableClient = _tableServiceClient.GetTableClient(jobStatusTableName);
            await tableClient.CreateIfNotExistsAsync();

            await res.WriteAsJsonAsync(new
            {
                status = "ok",
                blobs = new[] { imagesContainerName, outputContainerName },
                table = jobStatusTableName
            });

            return res;
        }
    }
}
