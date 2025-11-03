@description('Base name used to generate unique resource names.')
param baseName string = 'weatherimg'

@description('Azure location for all resources.')
param location string = resourceGroup().location

@description('Name of the blob container that stores generated images.')
param imagesContainerName string = 'images'

@description('Starter queue name (fan-in). Must match your code.')
param starterQueueName string = 'weather-jobs'

@description('Per-image processing queue name. Must match your code.')
param workerQueueName string = 'image-process3'

@description('Optional: public image API base URL (e.g., https://api.unsplash.com)')
@allowed([
  ''
])
param publicImageApiBase string = ''

@description('Optional: set to true to enable system-assigned managed identity on the Function App.')
param enableManagedIdentity bool = true

// ----------------- Naming helpers -----------------
var suffix = uniqueString(resourceGroup().id, baseName)
var storageName = toLower(replace('${baseName}${suffix}', '-', ''))
var planName = 'plan-${baseName}-${suffix}'
var functionAppName = 'func-${baseName}-${suffix}'

// ----------------- Storage Account -----------------
resource sa 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

// Blob container
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${sa.name}/default/${imagesContainerName}'
  properties: {
    // Set to 'Blob' for public anonymous read of blobs (or 'None' if you will use SAS).
    publicAccess: 'Blob'
  }
}

// Queues
resource starterQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = {
  name: '${sa.name}/default/${starterQueueName}'
}

resource workerQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = {
  name: '${sa.name}/default/${workerQueueName}'
}

// ----------------- App Service Plan (Consumption) -----------------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

// ----------------- Function App -----------------
resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        // Required by Functions
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        // Storage connection used by Functions runtime + your Queue/Blob bindings
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${sa.name};AccountKey=${listKeys(sa.id, ''2023-01-01'').keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        // Zip deploy: run from package
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        // App-specific settings (adjust to match your code)
        {
          name: 'IMAGES_CONTAINER_NAME'
          value: imagesContainerName
        }
        {
          name: 'STARTER_QUEUE_NAME'
          value: starterQueueName
        }
        {
          name: 'WORKER_QUEUE_NAME'
          value: workerQueueName
        }
        {
          name: 'BUIENRADAR_URL'
          value: 'https://data.buienradar.nl/2.0/feed/json'
        }
        // If you use a public image API (e.g., Unsplash). Keep key empty here; set via deploy script or portal.
        {
          name: 'PUBLIC_IMAGE_API_BASE'
          value: publicImageApiBase
        }
        {
          name: 'PUBLIC_IMAGE_API_KEY'
          value: ''
        }
      ]
      // .NET isolated doesn’t need a stack config here.
      http20Enabled: true
      alwaysOn: false
      ftpsState: 'Disabled'
    }
  }
  identity: enableManagedIdentity ? {
    type: 'SystemAssigned'
  } : null
  dependsOn: [
    sa
    starterQueue
    workerQueue
    blobContainer
    plan
  ]
}

// ----------------- Outputs -----------------
@description('Function App name.')
output functionAppName string = func.name

@description('Storage Account name.')
output storageAccountName string = sa.name

@description('Images container URL.')
output imagesContainerUrl string = 'https://${sa.name}.blob.${environment().suffixes.storage}/$imagesContainerName'

@description('Suggested start endpoint (adjust if your route differs).')
output exampleStartUrl string = 'https://${func.name}.azurewebsites.net/api/start'

@description('Suggested results endpoint (adjust if your route differs).')
output exampleResultsUrl string = 'https://${func.name}.azurewebsites.net/api/results/{jobId}'
