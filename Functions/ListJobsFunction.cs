using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WeatherImageApp.Helpers;

namespace WeatherImageApp.Functions
{
    public class ListJobsFunction
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _config;

        public ListJobsFunction(ILoggerFactory loggerFactory, IConfiguration config)
        {
            _logger = loggerFactory.CreateLogger<ListJobsFunction>();
            _config = config;
        }

        [Function("ListJobs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var top = 50;
            if (int.TryParse(query.Get("top"), out var parsed))
            {
                top = Math.Clamp(parsed, 1, 200);
            }

            var conn = _config["Storage:ConnectionString"];
            var tableName = _config["Storage:JobStatusTableName"] ?? "JobStatus";

            var res = req.CreateResponse(HttpStatusCode.OK);

            res.AddCors();

            if (string.IsNullOrWhiteSpace(conn))
            {
                res.StatusCode = HttpStatusCode.InternalServerError;
                await res.WriteAsJsonAsync(new { error = "Storage:ConnectionString not configured" });
                return res;
            }

            var tableClient = new TableClient(conn, tableName);

            var items = new List<object>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(maxPerPage: top))
            {
                items.Add(new
                {
                    id = entity.RowKey,
                    partitionKey = entity.PartitionKey,
                    status = entity.GetString("Status"),
                    createdAt = entity.GetDateTime("CreatedAt"),
                    updatedAt = entity.GetDateTime("UpdatedAt")
                });

                if (items.Count >= top)
                    break;
            }

            await res.WriteAsJsonAsync(new
            {
                count = items.Count,
                items
            });

            return res;
        }
    }
}
