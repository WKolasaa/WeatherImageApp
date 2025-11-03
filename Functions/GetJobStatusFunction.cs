using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WeatherImageApp.Helpers;

namespace WeatherImageApp.Functions
{
    public class GetJobStatusFunction
    {
        private readonly IConfiguration _config;
        private readonly ILogger<GetJobStatusFunction> _logger;

        public GetJobStatusFunction(IConfiguration config, ILogger<GetJobStatusFunction> logger)
        {
            _config = config;
            _logger = logger;
        }

        // GET http://localhost:7071/api/jobs/{jobId}
        [Function("GetJobStatus")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}")]
            HttpRequestData req,
            string jobId)
        {
            var connString = _config["Storage:ConnectionString"] ?? _config["AzureWebJobsStorage"];
            var tableName = _config["Storage:JobStatusTableName"] ?? "JobStatus";
            var imagesContainer = _config["IMAGE_OUTPUT_CONTAINER"] ?? _config["Storage:ImagesContainerName"] ?? "images";
            var sasExpiryMinutes = int.TryParse(_config["Api:SasExpiryMinutes"], out var mins) ? mins : 60;

            _logger.LogInformation("Getting status for job {JobId}", jobId);

            var tableClient = new TableClient(connString, tableName);
            await tableClient.CreateIfNotExistsAsync();

            TableEntity jobEntity;
            const string partitionKey = "jobs";

            try
            {
                var entityResponse = await tableClient.GetEntityAsync<TableEntity>(partitionKey, jobId);
                jobEntity = entityResponse.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new
                {
                    jobId,
                    status = "NotFound"
                });
                notFound.AddCors();
                return notFound;
            }

            var blobContainer = new BlobContainerClient(connString, imagesContainer);
            await blobContainer.CreateIfNotExistsAsync();

            var prefix = $"{jobId}/";
            var imageUrls = new List<string>();

            await foreach (var blobItem in blobContainer.GetBlobsAsync(prefix: prefix))
            {
                var sasUrl = BuildBlobSasUrl(blobContainer, blobItem.Name, connString, sasExpiryMinutes);
                imageUrls.Add(sasUrl);
            }

            var resp = req.CreateResponse(HttpStatusCode.OK);

            await resp.WriteAsJsonAsync(new
            {
                jobId,
                status = jobEntity.GetString("status"),
                createdAt = jobEntity.GetDateTime("createdAt"),
                updatedAt = jobEntity.GetDateTime("updatedAt"),
                images = imageUrls
            });

            resp.AddCors();
            return resp;
        }

        private static string BuildBlobSasUrl(
            BlobContainerClient containerClient,
            string blobName,
            string connectionString,
            int expiryMinutes)
        {
            var (accountName, accountKey, blobEndpoint) = ParseStorageInfo(connectionString);

            var credential = new StorageSharedKeyCredential(accountName, accountKey);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerClient.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();

            return $"{blobEndpoint}/{containerClient.Name}/{blobName}?{sas}";
        }

        private static (string accountName, string accountKey, string blobEndpoint) ParseStorageInfo(string connectionString)
        {
            if (string.Equals(connectionString, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            {
                const string devAccount = "devstoreaccount1";
                const string devKey =
                    "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
                const string devBlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1";
                return (devAccount, devKey, devBlobEndpoint);
            }

            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            string? accountName = null;
            string? accountKey = null;
            string? blobEndpoint = null;

            foreach (var part in parts)
            {
                if (part.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase))
                    accountName = part.Substring("AccountName=".Length);
                else if (part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase))
                    accountKey = part.Substring("AccountKey=".Length);
                else if (part.StartsWith("BlobEndpoint=", StringComparison.OrdinalIgnoreCase))
                    blobEndpoint = part.Substring("BlobEndpoint=".Length);
            }

            if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(accountKey))
                throw new InvalidOperationException("Storage connection string does not contain account name/key.");

            if (string.IsNullOrEmpty(blobEndpoint))
                blobEndpoint = $"https://{accountName}.blob.core.windows.net";

            return (accountName, accountKey, blobEndpoint);
        }
    }
}
