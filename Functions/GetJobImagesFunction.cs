using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class GetJobImagesFunction
    {
        private readonly IConfiguration _config;
        private readonly ILogger<GetJobImagesFunction> _logger;

        public GetJobImagesFunction(IConfiguration config, ILogger<GetJobImagesFunction> logger)
        {
            _config = config;
            _logger = logger;
        }

        // GET http://localhost:7071/api/jobs/{jobId}/images
        [Function("GetJobImages")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}/images")]
            HttpRequestData req,
            string jobId)
        {
            var connString = _config["Storage:ConnectionString"] ?? _config["AzureWebJobsStorage"];
            var containerName = _config["IMAGE_OUTPUT_CONTAINER"] ?? _config["Storage:ImagesContainerName"] ?? "images";
            var sasExpiryMinutes = int.TryParse(_config["Api:SasExpiryMinutes"], out var mins) ? mins : 60;

            _logger.LogInformation("Listing images for job {JobId} in container {Container}", jobId, containerName);

            // create client (works for real Azure and for Azurite)
            var containerClient = new BlobContainerClient(connString, containerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            // prefix is usually "jobId/filename.jpg"
            var prefix = $"{jobId}/";

            var blobs = new List<object>();

            await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
            {
                // build SAS url
                var sasUrl = BuildBlobSasUrl(containerClient, blobItem.Name, connString, sasExpiryMinutes);

                blobs.Add(new
                {
                    name = blobItem.Name.Replace(prefix, string.Empty),
                    blobName = blobItem.Name,
                    url = sasUrl
                });
            }

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(new
            {
                jobId,
                count = blobs.Count,
                images = blobs
            });

            return resp;
        }

        private static string BuildBlobSasUrl(
            BlobContainerClient containerClient,
            string blobName,
            string connectionString,
            int expiryMinutes)
        {
            // If we have account name/key we can build SAS. We must handle dev storage specially.
            var (accountName, accountKey, blobEndpoint) = ParseStorageInfo(connectionString);

            var credential = new StorageSharedKeyCredential(accountName, accountKey);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerClient.Name,
                BlobName = blobName,
                Resource = "b", // blob
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();

            // blobEndpoint is like http://127.0.0.1:10000/devstoreaccount1
            // we need to append container + blob
            var uri = new Uri($"{blobEndpoint}/{containerClient.Name}/{blobName}?{sas}");
            return uri.ToString();
        }

        /// <summary>
        /// Parses either a real Azure Storage connection string or the Azurite shortcut "UseDevelopmentStorage=true".
        /// </summary>
        private static (string accountName, string accountKey, string blobEndpoint) ParseStorageInfo(string connectionString)
        {
            if (string.Equals(connectionString, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            {
                // well-known Azurite/emulator values
                const string devAccount = "devstoreaccount1";
                const string devKey =
                    "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
                const string devBlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1";
                return (devAccount, devKey, devBlobEndpoint);
            }

            // real connection string: split on ;
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
            {
                // Fall back to default Azure endpoint
                blobEndpoint = $"https://{accountName}.blob.core.windows.net";
            }

            return (accountName, accountKey, blobEndpoint);
        }
    }
}
