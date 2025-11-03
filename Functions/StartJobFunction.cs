using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace WeatherImageApp.Functions
{
    public class StartJobFunction
    {
        private readonly ILogger<StartJobFunction> _logger;
        private readonly QueueServiceClient _queueSvc;

        public StartJobFunction(ILogger<StartJobFunction> logger, QueueServiceClient queueSvc)
        {
            _logger = logger;
            _queueSvc = queueSvc;
        }

        [Function("StartJobFunction")]
public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Admin, "post")] HttpRequestData req)
{
    var jobId = Guid.NewGuid().ToString();
    var weatherQ = _queueSvc.GetQueueClient("weather-jobs");
    await weatherQ.CreateIfNotExistsAsync();
    _logger.LogInformation("Queue ready: {QueueUri}", weatherQ.Uri);

    await weatherQ.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(jobId)));
    _logger.LogInformation("Job {JobId} queued to weather-jobs.", jobId);

    var res = req.CreateResponse(HttpStatusCode.OK);
    await res.WriteStringAsync(jobId);
    return res;
}
    }
}
