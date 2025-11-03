param(
    [string]$ResourceGroupName = "rg-weatherimg",
    [string]$Location = "westeurope",

    [string]$NamePrefix = "weatherimg",

    [string]$FunctionProjectPath = "./WeatherImageApp.csproj",

    [string]$BicepTemplatePath = "./bicep/main.bicep"
)

Write-Host "=== WeatherImageApp deploy ==="

Write-Host "[1/7] Checking Azure login..."
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "You are not logged in. Run 'az login' first." -ForegroundColor Red
    exit 1
}

Write-Host "[2/7] Ensuring resource group '$ResourceGroupName' in '$Location' ..."
az group create --name $ResourceGroupName --location $Location 1>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to create or get resource group." -ForegroundColor Red
    exit 1
}

Write-Host "[3/7] Deploying Bicep template '$BicepTemplatePath' ..."
$deploymentName = "$($NamePrefix)-deployment"

az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --template-file $BicepTemplatePath `
    --parameters location=$Location namePrefix=$NamePrefix `
    --output none

if ($LASTEXITCODE -ne 0) {
    Write-Host "Bicep deployment failed." -ForegroundColor Red
    exit 1
}

Write-Host "[4/7] Fetching function app name from deployment outputs..."
$funcName = az deployment group show `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --query "properties.outputs.functionAppName.value" `
    -o tsv

if (-not $funcName) {
    Write-Host "Could not fetch function app name from deployment outputs. Make sure your bicep outputs 'functionAppName'." -ForegroundColor Red
    exit 1
}

Write-Host "    Function App: $funcName"

Write-Host "[5/7] Publishing .NET project '$FunctionProjectPath' ..."
dotnet publish $FunctionProjectPath -c Release -o ./publish
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed." -ForegroundColor Red
    exit 1
}

Write-Host "[6/7] Zipping publish output..."
$zipPath = Join-Path (Get-Location) "publish.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory("./publish", $zipPath)

Write-Host "[7/7] Deploying zip to Function App '$funcName' ..."
az functionapp deployment source config-zip `
    --name $funcName `
    --resource-group $ResourceGroupName `
    --src $zipPath

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Deployment finished successfully ✅" -ForegroundColor Green
    Write-Host "Function App URL: https://$funcName.azurewebsites.net"
} else {
    Write-Host "Deployment failed." -ForegroundColor Red
    exit 1
}
