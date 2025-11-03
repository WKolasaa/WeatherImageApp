# WeatherImageApp

WeatherImageApp is an Azure Functions–based solution that ingests weather data, fans out work through Azure Storage Queues, renders weather information onto images, and exposes HTTP APIs to control and inspect that process. It is designed to demonstrate cloud-native patterns (HTTP → queue → queue → storage) and to match typical assignment requirements for Azure Functions, storage, and IaC.

---

## 1. Overview

The application consists of:

- **HTTP functions** to:
  - start a weather image generation job
  - query the status of a job
  - list all jobs
  - retrieve the images for a job
  - perform a health check

- **Queue-triggered functions** to:
  - fetch weather data and fan out work
  - generate and upload images, and update job status

- **Azure Storage resources** for:
  - queues: coordinating work
  - blobs: storing rendered images
  - tables: storing job status

- **Infrastructure as Code**:
  - a Bicep template (`bicep/main.bicep`) that creates the storage account, containers, queues, table, Application Insights, and the Function App
  - a PowerShell deployment script (`deploy.ps1`) that deploys the Bicep template and zip-deploys the function code

This repository is structured so it can be cloned, deployed, and evaluated in Azure with minimal manual setup.

---

## 2. Endpoints

When run locally (`func start`), the following HTTP endpoints are exposed:

- `GET  /api/health`  
  Liveness/readiness check for the function app.

- `POST /api/jobs/start`  
  `GET   /api/jobs/start`  
  Starts a new job that will generate images. Returns a JSON payload containing a `jobId`. This is the entrypoint for the whole flow.

- `GET  /api/jobs`  
  Returns a list of jobs from table storage.

- `GET  /api/jobs/{jobId}`  
  Returns the status of the given job, including processed/total counts and timestamps. This is how the client can check progress.

- `GET  /api/jobs/{jobId}/images`  
  Returns the images generated for the given job (blob names or SAS links, depending on configuration).

- `GET  /api/results/{jobId}`  
  Legacy/additional endpoint for result retrieval.

- `GET  /api/debug/queues`  
  Local helper to inspect queues. May not be exposed or needed in a production deployment.

Queue-triggered functions (not directly callable over HTTP):

- `ProcessWeatherStationFunction`  
  Triggered from the start queue. Fetches weather data and enqueues image-processing tasks.

- `ImageProcessFunction`  
  Triggered from the image queue. Generates the image, draws the text, uploads the blob, and updates job status.

These names match the functions shown in the local run output.

---

## 3. Processing Flow

1. **Start a job**  
   The client calls `POST /api/jobs/start`. The function:
   - creates or records a new job ID
   - enqueues a message on the start queue (`start-jobs`)
   - returns the job ID

2. **Process weather stations**  
   `ProcessWeatherStationFunction` is triggered by the start queue. It:
   - downloads the Buienradar JSON feed (`https://data.buienradar.nl/2.0/feed/json`)
   - reads the configured number of stations (from `Api:StationsToProcess`, e.g. 50)
   - for each station, enqueues a message on the image queue (`image-jobs`) containing:
     - JobId
     - StationName
     - Temperature (if available)

3. **Generate images**  
   `ImageProcessFunction` is triggered by the image queue. It:
   - loads the base image from `assets/base-weather.jpg`
   - loads the font from `assets/OpenSens-Regular.ttf`
   - uses ImageSharp to draw `"{StationName}: {Temperature}°C"` onto the image
   - uploads the result to Azure Blob Storage in the `images` container, under the path `{jobId}/{stationName}.jpg`
   - updates the `JobStatus` table entry for this job to mark progress (`ProcessedStations`, `Status`, `LastUpdatedUtc`, etc.)

4. **Query status / images**  
   The client can call:
   - `GET /api/jobs/{jobId}` to see status and progress
   - `GET /api/jobs/{jobId}/images` to see which images are available for this job

This flow decouples the user-facing endpoint from the long-running work, which is one of the main goals of the assignment.

---

## 4. Configuration

### 4.1 Local configuration

Local development uses `local.settings.json`. The relevant values are:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",

    "Storage:ConnectionString": "UseDevelopmentStorage=true",
    "Storage:ImagesContainerName": "images",
    "Storage:StartQueueName": "start-jobs",
    "Storage:ImageQueueName": "image-jobs",
    "Storage:JobStatusTableName": "JobStatus",

    "Api:BuienradarUrl": "https://data.buienradar.nl/2.0/feed/json",
    "Api:UnsplashQuery": "weather",
    "Api:SasExpiryMinutes": "60",
    "Api:StationsToProcess": "50",
    "Api:ImageApiUrl": "https://picsum.photos/600/400",
    "Api:UnsplashApiKey": "",

    "IMAGE_OUTPUT_CONTAINER": "images"
  }
}
````

This file:

* sets the same queue names the Functions are configured to listen to
* sets the same container name the app writes to
* uses Azurite (`UseDevelopmentStorage=true`) for local runs
* sets the Buienradar API URL the code will call

### 4.2 Azure configuration

The Bicep template mirrors these settings into the Function App’s application settings, but with a **real** storage connection string. It also creates the storage constructs the code expects:

* Blob containers:

  * `images` (actual output)
  * `output` (optional)
* Queues:

  * `start-jobs`
  * `image-jobs`
* Table:

  * `JobStatus`

The Function App then reads these from configuration the same way it does locally, so no code changes are required between local and Azure.

---

## 5. Infrastructure as Code (Bicep)

`bicep/main.bicep`:

* Accepts:

  * `location` (defaults to the resource group’s location)
  * `namePrefix` (used to prefix the function app, plan, and insights names)
* Creates:

  * a **StorageV2** account (Standard_LRS, HTTPS only)
  * blob containers (`images`, `output`)
  * queues (`start-jobs`, `image-jobs`)
  * table service + table (`JobStatus`)
  * Application Insights instance
  * a **Windows** Function App on a **consumption** plan
* Sets application settings on the Function App:

  * all `Storage:*` keys
  * all `Api:*` keys
  * `IMAGE_OUTPUT_CONTAINER=images`
  * `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`
  * `WEBSITE_RUN_FROM_PACKAGE=1`
  * `WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED=1`
* Outputs:

  * `functionAppName`
  * `storageAccountName`
  * container names

These outputs are used by the PowerShell deploy script to know where to send the published package.

---

## 6. Deployment Script

`deploy.ps1` orchestrates deployment in the following order:

1. Verify Azure CLI is logged in.
2. Create or update the resource group.
3. Deploy the Bicep template with the given location and name prefix.
4. Read the `functionAppName` from the deployment output.
5. Run `dotnet publish` on the functions project (`WeatherImageApp.csproj`).
6. Zip the published output.
7. Call `az functionapp deployment source config-zip` to upload the zip to the created Function App.

Example invocation:

```powershell
.\deploy.ps1 -ResourceGroupName rg-weatherimg -Location westeurope -NamePrefix weatherimg
```

This makes the deployment reproducible and demonstrable, which is often part of the grading criteria.

---

## 7. HTTP Test File

The repository can include an HTTP file (e.g. `api.http`) to quickly test all endpoints against Azure:

```http
@host = https://weatherimageapp-win-gdhah5b9graacuh8.polandcentral-01.azurewebsites.net

###
# 1) Health check
GET {{host}}/api/health
Accept: application/json

###
# 2) Start a new job
# response is named so it can be reused as the jobId below
# @name startJob
POST {{host}}/api/jobs/start
Content-Type: application/json

{}

###
# 3) Get status for the job just started
# This uses the jobId from the response above
GET {{host}}/api/jobs/{{startJob.response.body.$.jobId}}
Accept: application/json

###
# 4) Get images for the started job 
GET {{host}}/api/jobs/{{startJob.response.body.$.jobId}}/images
Accept: application/json

###
# 5) List all jobs
GET {{host}}/api/jobs
Accept: application/json

###
# 6) Get results (legacy / optional) for the job  just started
GET {{host}}/api/results/{{startJob.response.body.$.jobId}}
Accept: application/json

###
# 7) Debug queues (may only work locally or if exposed)
GET {{host}}/api/debug/queues
Accept: application/json
```

This serves as documentation and as a quick verification tool for evaluators.

---

## 8. Image Rendering

The image rendering step is implemented in `ImageProcessFunction` and uses:

* the base image shipped with the app (`assets/base-weather.jpg`)
* the font shipped with the app (`assets/OpenSens-Regular.ttf`)
* ImageSharp and related packages already referenced in `WeatherImageApp.csproj`

The project file already contains:

```xml
<Content Include="assets\**\*.*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

so the assets are actually deployed to Azure alongside the binaries. This removes the common problem where text renders locally but not in Azure (because of missing fonts).

---

