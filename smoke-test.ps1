<# 
.SYNOPSIS
    Smoke tests for FunctionalWebApi against a running server

.USAGE
    .\smoke-test.ps1 [-BaseUrl <url>]

.EXAMPLE
    .\smoke-test.ps1
    .\smoke-test.ps1 -BaseUrl "http://localhost:5050"
    .\smoke-test.ps1 -BaseUrl "http://myapi.example.com"
#>

param(
    [string]$BaseUrl = "http://localhost:5050"
)

$ErrorActionPreference = "Stop"
$URL = $BaseUrl.TrimEnd('/')

Write-Host "Testing against $URL"

# Helper: invoke and show status/body
function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Method,
        [string]$Path,
        [string]$Body = $null,
        [string]$AuthToken = $null,
        [int[]]$ExpectedStatus = @(200)
    )
    Write-Host "`n== $Name =="
    try {
        $headers = @{}
        if ($AuthToken) { $headers.Authorization = "Bearer $AuthToken" }
        $resp = Invoke-RestMethod -Uri "$URL$Path" -Method $Method -ContentType "application/json" `
            -Headers $headers -Body $Body -StatusCodeVariable statusCode
        $statusCode
        if ($resp -ne $null) { $resp | ConvertTo-Json -Depth 5 }
        if ($ExpectedStatus -notcontains $statusCode) {
            Write-Warning "Expected status in $($ExpectedStatus -join ', '), got $statusCode"
        }
        return @{ StatusCode = $statusCode; Response = $resp }
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        Write-Host "status=$status"
        if ($ExpectedStatus -notcontains $status) {
            Write-Warning "Expected status in $($ExpectedStatus -join ', '), got $status"
        }
        return @{ StatusCode = $status; Response = $null }
    }
}

# ---- smoke tests ----

# create user
$create = Test-Endpoint "create" "Post" "/users" `
    '{"name":"Alice","email":"alice@example.com","password":"hunter2","confirmPassword":"hunter2"}'

# get by id
Test-Endpoint "get-by-id" "Get" "/users/1"

# list
Test-Endpoint "list" "Get" "/users"

# login (capture token)
$login = Test-Endpoint "login" "Post" "/login" `
    '{"username":"alice@example.com","password":"hunter2"}'
$global:TOKEN = $login.Response.token

# login wrong (expect 401)
Test-Endpoint "login wrong" "Post" "/login" `
    '{"username":"alice@example.com","password":""}' -ExpectedStatus @(401)

# change password (requires auth)
$cp = Test-Endpoint "change-password" "Put" "/users/1/password" `
    '{"currentPassword":"hunter2","newPassword":"new-secret","confirmNewPassword":"new-secret"}' `
    -AuthToken $global:TOKEN

# login with new password
Test-Endpoint "login with new" "Post" "/login" `
    '{"username":"alice@example.com","password":"new-secret"}'

Write-Host "`nAll smoke tests passed!"