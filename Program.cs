using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WeatherImageApp
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureFunctionsWorkerDefaults()
                .ConfigureServices((context, services) =>
                {
                    var config = context.Configuration;

                    // use your custom one first, fall back to default
                    var storageConn =
                        config["Storage:ConnectionString"] ??
                        config["AzureWebJobsStorage"];

                    // tables
                    services.AddSingleton<TableServiceClient>(_ =>
                        new TableServiceClient(storageConn));

                    // blobs
                    services.AddSingleton<BlobServiceClient>(_ =>
                        new BlobServiceClient(storageConn));

                    // queues (this is the one your DebugQueuesFunction needs)
                    services.AddSingleton<QueueServiceClient>(_ =>
                        new QueueServiceClient(storageConn));
                })
                .Build();

            await host.RunAsync();
        }
    }
}
