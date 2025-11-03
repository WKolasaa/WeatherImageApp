using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class GetJobImagesFunction
    {
        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;

        public GetJobImagesFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GetJobImagesFunction>();

            var storageConn = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            _blobServiceClient = new BlobServiceClient(storageConn);
        }

        [Function("GetJobImages")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}/images")]
            HttpRequestData req,
            string jobId)
        {
            _logger.LogInformation("GetJobImages called for job {JobId}", jobId);

            var container = await FindContainerForJobAsync(jobId);
            if (container == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new
                {
                    error = "container-not-found",
                    message = "Could not find a blob container that contains this job's images.",
                    jobId
                });
                return notFound;
            }

            var imageUrls = new List<string>();
            await foreach (var blob in container.GetBlobsAsync(prefix: $"{jobId}/"))
            {
                var blobClient = container.GetBlobClient(blob.Name);
                imageUrls.Add(blobClient.Uri.ToString());
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new
            {
                jobId,
                container = container.Name,
                count = imageUrls.Count,
                images = imageUrls
            });
            return ok;
        }

        private async Task<BlobContainerClient?> FindContainerForJobAsync(string jobId)
        {
            var overrideName = Environment.GetEnvironmentVariable("IMAGE_OUTPUT_CONTAINER");
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                var candidate = _blobServiceClient.GetBlobContainerClient(overrideName);
                if (await candidate.ExistsAsync())
                    return candidate;
            }

            var guesses = new[] { "output", "weather-images", "images" };
            foreach (var name in guesses)
            {
                var candidate = _blobServiceClient.GetBlobContainerClient(name);
                if (await candidate.ExistsAsync())
                {
                    await foreach (var _ in candidate.GetBlobsAsync(prefix: $"{jobId}/"))
                    {
                        return candidate;
                    }
                }
            }

            await foreach (var containerItem in _blobServiceClient.GetBlobContainersAsync())
            {
                var candidate = _blobServiceClient.GetBlobContainerClient(containerItem.Name);
                await foreach (var _ in candidate.GetBlobsAsync(prefix: $"{jobId}/"))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
