using Microsoft.Azure.Functions.Worker.Http;

namespace WeatherImageApp.Helpers
{
    public static class HttpResponseExtensions
    {
        public static void AddCors(this HttpResponseData response, string origin = "*")
        {
            response.Headers.Add("Access-Control-Allow-Origin", origin);
            response.Headers.Add("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }
    }
}
