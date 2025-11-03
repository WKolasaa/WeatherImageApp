using System;
using System.Net;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class GetJobStatusFunction
    {
        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;

        private const int ExpectedImagesPerJob = 36;

        public GetJobStatusFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GetJobStatusFunction>();

            var storageConn = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            _blobServiceClient = new BlobServiceClient(storageConn);
        }

        [Function("GetJobStatus")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}/status")]
            HttpRequestData req,
            string jobId)
        {
            _logger.LogInformation("Getting status for job {JobId}", jobId);

            var container = await FindContainerForJobAsync(jobId);
            if (container == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new
                {
                    error = "container-not-found",
                    message = "Could not find a blob container that contains this job's images.",
                    jobId,
                    expectedImages = ExpectedImagesPerJob
                });
                return notFound;
            }

            var processed = 0;
            await foreach (var _ in container.GetBlobsAsync(prefix: $"{jobId}/"))
            {
                processed++;
            }

            var percent = ExpectedImagesPerJob == 0
                ? 100
                : Math.Min(100, (int)Math.Round((double)processed / ExpectedImagesPerJob * 100));
            var status = processed >= ExpectedImagesPerJob ? "Completed" : "InProgress";

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new
            {
                jobId,
                status,
                processedImages = processed,
                expectedImages = ExpectedImagesPerJob,
                percentComplete = percent,
                container = container.Name,
                imagesEndpoint = $"/api/jobs/{jobId}/images"
            });
            return res;
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
