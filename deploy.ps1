<# 
Deploys the WeatherImageApp:
 - Builds the .NET isolated Azure Functions app
 - Provisions Azure resources via Bicep
 - Sets app settings (optionally your public image API key)
 - Zip-deploys the package to the Function App

Prereqs:
 az login
 az account set -s "<subscription id/name>"
 Azure Functions Core Tools NOT required (we use az zip deploy)
 .NET 8 SDK

Usage:
 ./deploy.ps1 -ResourceGroup rg-weatherimg -Location westeurope `
              -ProjectPath "./WeatherImageApp/WeatherImageApp.csproj" `
              -BicepFile "./WeatherImageApp/bicep/main.bicep" `
              -BaseName "weatherimg" `
              -PublicImageApiBase "https://api.unsplash.com" `
              -PublicImageApiKey "<YOUR_KEY>"
#>

param(
  [Parameter(Mandatory=$true)]
  [string]$ResourceGroup,

  [Parameter(Mandatory=$true)]
  [string]$Location,

  [string]$ProjectPath = "./WeatherImageApp/WeatherImageApp.csproj",

  [string]$BicepFile = "./WeatherImageApp/bicep/main.bicep",

  [string]$BaseName = "weatherimg",

  [string]$PublicImageApiBase = "",

  [string]$PublicImageApiKey = ""
)

$ErrorActionPreference = "Stop"

Write-Host "==> Creating/validating resource group '$ResourceGroup' in $Location..."
az group create -n $ResourceGroup -l $Location | Out-Null

# ---------- Build & package ----------
$publishDir = Join-Path (Resolve-Path ".").Path "publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $ProjectPath -c Release -o $publishDir

# Create a zip for zip-deploy
$zipPath = Join-Path $publishDir "app.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

# ---------- Provision with Bicep ----------
Write-Host "==> Deploying Bicep template..."
$deploymentName = "weatherimg-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$deploy = az deployment group create `
  -g $ResourceGroup `
  -n $deploymentName `
  --template-file $BicepFile `
  --parameters baseName=$BaseName location=$Location publicImageApiBase=$PublicImageApiBase `
  --query "properties.outputs" -o json | ConvertFrom-Json

$functionAppName   = $deploy.functionAppName.value
$storageAccount    = $deploy.storageAccountName.value
$imagesContainerUrl = $deploy.imagesContainerUrl.value

Write-Host "==> Provisioned:"
Write-Host "    Function App: $functionAppName"
Write-Host "    Storage     : $storageAccount"
Write-Host "    Images URL  : $imagesContainerUrl"

# ---------- Optional app settings (API key, etc.) ----------
$settings = @()
if ($PublicImageApiBase -and $PublicImageApiBase.Trim() -ne "") {
  $settings += "PUBLIC_IMAGE_API_BASE=$PublicImageApiBase"
}
if ($PublicImageApiKey -and $PublicImageApiKey.Trim() -ne "") {
  $settings += "PUBLIC_IMAGE_API_KEY=$PublicImageApiKey"
}
if ($settings.Count -gt 0) {
  Write-Host "==> Setting additional app settings..."
  az functionapp config appsettings set `
    -g $ResourceGroup -n $functionAppName `
    --settings $settings | Out-Null
}

# ---------- Zip deploy ----------
Write-Host "==> Zip-deploying package..."
az functionapp deployment source config-zip `
  -g $ResourceGroup -n $functionAppName `
  --src $zipPath | Out-Null

# ---------- Output helpful endpoints ----------
$startUrl   = "https://$functionAppName.azurewebsites.net/api/start"
$resultsUrl = "https://$functionAppName.azurewebsites.net/api/results/{jobId}"

Write-Host ""
Write-Host "✅ Deploy complete."
Write-Host "Try the endpoints (adjust if your routes differ):"
Write-Host "  POST $startUrl"
Write-Host "  GET  $resultsUrl"
