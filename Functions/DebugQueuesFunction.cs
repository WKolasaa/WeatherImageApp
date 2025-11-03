// Functions/DebugQueuesFunction.cs
using System.Net;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace WeatherImageApp.Functions
{
    public class DebugQueuesFunction
    {
        private readonly QueueServiceClient _svc;
        public DebugQueuesFunction(QueueServiceClient svc) => _svc = svc;

        [Function("DebugQueues")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Admin, "get", Route = "debug/queues")] HttpRequestData req)
        {
            async Task<object> One(string name)
            {
                var q = _svc.GetQueueClient(name);
                await q.CreateIfNotExistsAsync();
                var props = await q.GetPropertiesAsync();
                return new { name, approximateMessagesCount = props.Value.ApproximateMessagesCount, uri = q.Uri.ToString() };
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            var payload = new
            {
                weather = await One("weather-jobs"),
                image = await One("image-process3"),
                weatherPoison = await One("weather-jobs-poison"),
                imagePoison = await One("image-process3-poison")
            };
            await res.WriteAsJsonAsync(payload);
            return res;
        }
    }
}
