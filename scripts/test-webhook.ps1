<#
.SYNOPSIS
  Simulate a signed GitHub webhook event against an Umbraco Automate webhook URL.

.DESCRIPTION
  Builds a sample GitHub `issues` payload, signs it with HMAC-SHA256 (the scheme the
  UmbHost.Automate.GitHub authenticator validates via the X-Hub-Signature-256 header),
  and POSTs it to the automation's webhook URL — so you can test end-to-end locally
  without exposing localhost to GitHub.

.EXAMPLE
  ./test-webhook.ps1 -WebhookUrl 'https://localhost:44300/umbraco/automate/webhook/<guid>' -Secret 'testsecret'

.EXAMPLE
  ./test-webhook.ps1 -WebhookUrl '<url>' -Secret 'testsecret' -Action opened `
      -Repo 'UmbHost/test' -Number 42 -Title 'Test issue'
#>
param(
    [Parameter(Mandatory = $true)][string]$WebhookUrl,
    [Parameter(Mandatory = $true)][string]$Secret,
    [string]$Action = 'opened',
    [string]$Repo = 'UmbHost/test',
    [int]$Number = 42,
    [string]$Title = 'Test issue from local'
)

$owner, $name = $Repo.Split('/', 2)
$payload = [ordered]@{
    action     = $Action
    issue      = [ordered]@{
        number   = $Number
        title    = $Title
        html_url = "https://github.com/$Repo/issues/$Number"
    }
    repository = [ordered]@{ full_name = $Repo; name = $name; owner = @{ login = $owner } }
}
$body = $payload | ConvertTo-Json -Compress -Depth 6

$hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
$hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body))
$signature = 'sha256=' + ([BitConverter]::ToString($hash) -replace '-', '').ToLower()

Write-Host "POST $WebhookUrl"
Write-Host "X-Hub-Signature-256: $signature"
Write-Host "Body: $body"

try {
    $response = Invoke-WebRequest -Uri $WebhookUrl -Method Post -ContentType 'application/json' `
        -Headers @{ 'X-Hub-Signature-256' = $signature } -Body $body -SkipCertificateCheck
    Write-Host "-> $([int]$response.StatusCode) $($response.StatusDescription)" -ForegroundColor Green
}
catch {
    Write-Host "-> request failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) { Write-Host "   status: $([int]$_.Exception.Response.StatusCode)" }
}
