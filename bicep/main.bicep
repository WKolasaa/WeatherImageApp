@description('Azure region to deploy to')
param location string = resourceGroup().location

@description('Prefix for naming resources')
param namePrefix string = 'weatherimg'

var storageAccountName = 'st${uniqueString(resourceGroup().id)}'
var functionAppName    = '${namePrefix}-func'
var hostingPlanName    = '${namePrefix}-plan'
var appInsightsName    = '${namePrefix}-ai'

// --- STORAGE ACCOUNT ---
resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

// --- BLOBS: containers ---
resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storage.name}/default/images'
  properties: {
    publicAccess: 'None'
  }
}

resource outputContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storage.name}/default/output'
  properties: {
    publicAccess: 'None'
  }
}

// --- QUEUES ---
resource startJobsQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = {
  name: '${storage.name}/default/start-jobs'
}

resource imageJobsQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = {
  name: '${storage.name}/default/image-jobs'
}

// --- TABLE for job status ---
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' = {
  name: '${storage.name}/default'
}

resource jobStatusTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  name: '${storage.name}/default/JobStatus'
  dependsOn: [
    tableService
  ]
}

// --- APP INSIGHTS ---
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

// --- CONSUMPTION PLAN (Functions) ---
resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: hostingPlanName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

// we need the storage connection string for app settings
var storageKey = listKeys(storage.name, storage.apiVersion).keys[0].value
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storageKey};EndpointSuffix=${environment().suffixes.storage}'

// --- FUNCTION APP ---
resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: storageConnectionString
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsights.properties.InstrumentationKey
        }
        // your custom settings mirrored from local.settings.json
        {
          name: 'Storage:ConnectionString'
          value: storageConnectionString
        }
        {
          name: 'Storage:ImagesContainerName'
          value: 'images'
        }
        {
          name: 'Storage:StartQueueName'
          value: 'start-jobs'
        }
        {
          name: 'Storage:ImageQueueName'
          value: 'image-jobs'
        }
        {
          name: 'Storage:JobStatusTableName'
          value: 'JobStatus'
        }
        {
          name: 'Api:BuienradarUrl'
          value: 'https://data.buienradar.nl/2.0/feed/json'
        }
        {
          name: 'Api:UnsplashQuery'
          value: 'weather'
        }
        {
          name: 'Api:SasExpiryMinutes'
          value: '60'
        }
        {
          name: 'Api:StationsToProcess'
          value: '50'
        }
        {
          name: 'Api:ImageApiUrl'
          value: 'https://picsum.photos/600/400'
        }
        {
          name: 'Api:UnsplashApiKey'
          value: ''
        }
        {
          name: 'IMAGE_OUTPUT_CONTAINER'
          value: 'output'
        }
      ]
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'project': namePrefix
  }
}

output functionAppName string = functionApp.name
output storageAccountName string = storage.name
output imagesContainerName string = imagesContainer.name
output outputContainerName string = outputContainer.name
