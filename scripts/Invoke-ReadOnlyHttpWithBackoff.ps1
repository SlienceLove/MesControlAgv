[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Uri,

    [ValidateRange(1, 3)]
    [int]$MaxAttempts = 2,

    [ValidateRange(1, 3600)]
    [int]$RetryDelaySeconds = 30,

    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
try {
    $results = foreach ($address in $Uri) {
        $lastError = $null
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            try {
                $request = [System.Net.Http.HttpRequestMessage]::new(
                    [System.Net.Http.HttpMethod]::Get,
                    $address)
                $response = $client.SendAsync($request).GetAwaiter().GetResult()
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $status = [int]$response.StatusCode
                if ($response.IsSuccessStatusCode) {
                    [pscustomobject]@{
                        uri = $address
                        status = $status
                        attempt = $attempt
                        body = $body
                    }
                    $response.Dispose()
                    $request.Dispose()
                    $lastError = $null
                    break
                }

                $lastError = "HTTP $status"
                $retryable = $status -eq 429 -or $status -ge 500
                $response.Dispose()
                $request.Dispose()
                if (-not $retryable -or $attempt -ge $MaxAttempts) {
                    throw "GET $address failed with HTTP $status."
                }
            }
            catch {
                $lastError = $_.Exception.Message
                if ($attempt -ge $MaxAttempts) { throw }
                Write-Warning "Read-only GET attempt $attempt for '$address' failed: $lastError"
                Write-Warning "Waiting $RetryDelaySeconds seconds before retry; no state-changing request is retried."
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }
        if ($null -ne $lastError) { throw $lastError }
    }
    $results | ConvertTo-Json -Depth 30
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
