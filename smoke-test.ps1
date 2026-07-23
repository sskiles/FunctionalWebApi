<# 
.SYNOPSIS
    Smoke tests for FunctionalWebApi AOT binary

.USAGE
    .\smoke-test.ps1 [-BinaryPath <path>]

.EXAMPLE
    .\smoke-test.ps1
    .\smoke-test.ps1 -BinaryPath ".\bin\Release\net10.0\linux-x64\publish\FunctionalWebApi"
#>

param(
    [string]$BinaryPath = ".\bin\Release\net10.0\linux-x64\publish\FunctionalWebApi"
)

$ErrorActionPreference = "Stop"
$DB = "$env:TEMP\webapi_smoke.db"
$URL = "http://localhost:5050"

if (-not (Test-Path $BinaryPath)) {
    Write-Error "Binary not found at $BinaryPath"
    Write-Host "Build first: dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64"
    exit 1
}

function Cleanup {
    if ($script:appProcess) {
        Stop-Process -Id $script:appProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $DB) { Remove-Item $DB -Force }
}
Register-EngineEvent -SourceIdentifier "PowerShell.Exiting" -Action { Cleanup } | Out-Null

if (Test-Path $DB) { Remove-Item $DB -Force }

Write-Host "Starting $BinaryPath ..."
$env:ASPNETCORE_URLS = $URL
$env:ConnectionStrings__Sqlite = "Data Source=$DB"
$script:appProcess = Start-Process -FilePath $BinaryPath -PassThru -WindowStyle Hidden

# Wait for server to be ready
for ($i = 0; $i -lt 10; $i++) {
    try {
        Invoke-RestMethod -Uri "$URL/users" -Method Get -ErrorAction Stop | Out-Null
        break
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "`n== create =="
$createResp = Invoke-RestMethod -Uri "$URL/users" -Method Post -ContentType "application/json" `
    -Body '{"name":"Alice","email":"alice@example.com","password":"hunter2","confirmPassword":"hunter2"}'
$createResp | ConvertTo-Json -Depth 5

Write-Host "`n== get-by-id =="
Invoke-RestMethod -Uri "$URL/users/1" -Method Get | ConvertTo-Json -Depth 5

Write-Host "`n== list =="
Invoke-RestMethod -Uri "$URL/users" -Method Get | ConvertTo-Json -Depth 5

Write-Host "`n== login =="
$loginResp = Invoke-RestMethod -Uri "$URL/login" -Method Post -ContentType "application/json" `
    -Body '{"username":"alice@example.com","password":"hunter2"}'
$token = $loginResp.token
$token

Write-Host "`n== login wrong (expect 401) =="
try {
    Invoke-RestMethod -Uri "$URL/login" -Method Post -ContentType "application/json" `
        -Body '{"username":"alice@example.com","password":""}' | Out-Null
}
catch {
    $_.Exception.Response.StatusCode.value__
}

Write-Host "`n== change-password =="
$cpResp = Invoke-RestMethod -Uri "$URL/users/1/password" -Method Put -ContentType "application/json" `
    -Headers @{Authorization = "Bearer $token"} `
    -Body '{"currentPassword":"hunter2","newPassword":"new-secret","confirmNewPassword":"new-secret"}'
$cpResp | ConvertTo-Json -Depth 5

Write-Host "`n== login with new password =="
$lnResp = Invoke-RestMethod -Uri "$URL/login" -Method Post -ContentType "application/json" `
    -Body '{"username":"alice@example.com","password":"new-secret"}'
$lnResp | ConvertTo-Json -Depth 5

Write-Host "`nAll smoke tests passed!"
Cleanup