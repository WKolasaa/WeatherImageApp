param(
    [string]$ResourceGroupName = "rg-weatherimg",
    [string]$Location = "westeurope",
    [string]$NamePrefix = "weatherimg"
)

# 1. Make sure you are logged in
Write-Host "Checking Azure login..."
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "You are not logged in. Run 'az login' first." -ForegroundColor Red
    exit 1
}

# 2. Create resource group (idempotent)
Write-Host "Creating / updating resource group $ResourceGroupName in $Location ..."
az group create --name $ResourceGroupName --location $Location 1>$null

# 3. Deploy bicep
Write-Host "Deploying Bicep template..."
$deploymentName = "weatherimg-deployment"
az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --template-file ./main.bicep `
    --parameters location=$Location namePrefix=$NamePrefix `
    --output none

# 4. Get function app name from deployment outputs
Write-Host "Fetching function app name from deployment..."
$funcName = az deployment group show `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --query "properties.outputs.functionAppName.value" `
    -o tsv

if (-not $funcName) {
    Write-Host "Could not fetch function app name from deployment outputs." -ForegroundColor Red
    exit 1
}

Write-Host "Function App: $funcName"

# 5. Build / publish the function project
Write-Host "Publishing .NET project..."
# adjust path to your .csproj if needed
dotnet publish ./WeatherImageApp.csproj -c Release -o ./publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed." -ForegroundColor Red
    exit 1
}

# 6. Zip the publish folder
Write-Host "Zipping publish output..."
$zipPath = Join-Path (Get-Location) "publish.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory("./publish", $zipPath)

# 7. Deploy zip to function app
Write-Host "Deploying zip to Function App..."
az functionapp deployment source config-zip `
    --name $funcName `
    --resource-group $ResourceGroupName `
    --src $zipPath

if ($LASTEXITCODE -eq 0) {
    Write-Host "Deployment finished successfully ✅" -ForegroundColor Green
    Write-Host "Function App URL (root): https://$funcName.azurewebsites.net"
} else {
    Write-Host "Deployment failed." -ForegroundColor Red
}
