using System;
using Azure;
using Azure.Data.Tables;

namespace WeatherImageApp.Models
{
    public class JobStatusEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "jobs";
        public string RowKey { get; set; } = default!; // jobId

        public string Status { get; set; } = "Created";
        public int TotalStations { get; set; }
        public int ProcessedStations { get; set; }
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

        public ETag ETag { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
