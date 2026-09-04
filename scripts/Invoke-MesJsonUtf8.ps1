[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE')]
    [string]$Method,

    [Parameter(Mandatory = $true)]
    [string]$Uri,

    [AllowNull()]
    [string]$Json = $null,

    [string]$JsonPath = $null,

    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 30,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# This helper is intentionally a one-shot client. It exists for field
# preparation and workflow publication, where a failed request must stop the
# run rather than risk replaying a state-changing operation.
$jsonArgumentProvided = $PSBoundParameters.ContainsKey('Json')
$jsonPathArgumentProvided = $PSBoundParameters.ContainsKey('JsonPath')
if ($jsonArgumentProvided -and $jsonPathArgumentProvided) {
    throw 'Specify either -Json or -JsonPath, not both.'
}

$httpMethod = $Method.Trim().ToUpperInvariant()
$uriValue = $null
if (-not [Uri]::TryCreate($Uri.Trim(), [UriKind]::Absolute, [ref]$uriValue) -or
    $uriValue.Scheme -notin @('http', 'https')) {
    throw "Uri must be an absolute http/https address: '$Uri'."
}

$bodyText = $null
if ($jsonPathArgumentProvided -and -not [string]::IsNullOrWhiteSpace($JsonPath)) {
    $absoluteJsonPath = [IO.Path]::GetFullPath($JsonPath)
    if (-not (Test-Path -LiteralPath $absoluteJsonPath -PathType Leaf)) {
        throw "JSON body file was not found: '$absoluteJsonPath'."
    }
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $bodyText = [IO.File]::ReadAllText($absoluteJsonPath, $strictUtf8)
}
elseif ($jsonArgumentProvided) {
    $bodyText = $Json
}

if ($httpMethod -in @('GET', 'HEAD') -and $null -ne $bodyText) {
    throw "$httpMethod requests must not include a JSON body."
}

if ($null -ne $bodyText) {
    # Windows PowerShell ConvertTo-Json emits this legacy .NET date shape for
    # DateTime/DateTimeOffset values. MES expects an ISO-8601 string instead;
    # fail early rather than send a request that will be rejected ambiguously.
    if ($bodyText -match '(?i)/Date\([^)]*\)/') {
        throw 'JSON contains /Date(...)/. Serialize DateTime values as ISO-8601 (for example 2026-09-04T01:00:00Z) before sending.'
    }

    # Validate the payload without round-tripping it through ConvertFrom/To-Json;
    # preserving the original string is what guarantees exact UTF-8 bytes. The
    # JavaScriptSerializer path is available in Windows PowerShell 5.1; the
    # ConvertFrom-Json fallback keeps the helper usable from PowerShell 7 too.
    try {
        Add-Type -AssemblyName System.Web.Extensions
        $jsonValidator = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $null = $jsonValidator.DeserializeObject($bodyText)
    }
    catch {
        $null = $bodyText | ConvertFrom-Json
    }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$request = $null
$content = $null
$timeout = New-Object System.Threading.CancellationTokenSource
$timeout.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))

try {
    $request = New-Object System.Net.Http.HttpRequestMessage(
        ([System.Net.Http.HttpMethod]::new($httpMethod)),
        $uriValue)

    if ($null -ne $bodyText) {
        $bodyBytes = [Text.Encoding]::UTF8.GetBytes($bodyText)
        $content = New-Object System.Net.Http.ByteArrayContent(,$bodyBytes)
        $mediaType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/json')
        $mediaType.CharSet = 'utf-8'
        $content.Headers.ContentType = $mediaType
        $request.Content = $content
    }

    # Exactly one SendAsync call is made. Do not add retry behavior here.
    $response = $client.SendAsync(
        $request,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
        $timeout.Token).GetAwaiter().GetResult()
    try {
        $responseBytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $responseText = [Text.Encoding]::UTF8.GetString($responseBytes)

        if (-not $response.IsSuccessStatusCode) {
            throw "MES HTTP $([int]$response.StatusCode) $($response.ReasonPhrase): $responseText"
        }

        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $absoluteOutputPath = [IO.Path]::GetFullPath($OutputPath)
            if (Test-Path -LiteralPath $absoluteOutputPath) {
                throw "Refusing to overwrite existing response file '$absoluteOutputPath'."
            }
            $parent = Split-Path -Parent $absoluteOutputPath
            if (-not [string]::IsNullOrWhiteSpace($parent) -and
                -not (Test-Path -LiteralPath $parent -PathType Container)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
            }
            [IO.File]::WriteAllBytes($absoluteOutputPath, $responseBytes)
        }

        Write-Output $responseText
    }
    finally {
        $response.Dispose()
    }
}
catch [System.OperationCanceledException] {
    if ($timeout.IsCancellationRequested) {
        throw "MES HTTP $httpMethod $($uriValue.AbsolutePath) exceeded $TimeoutSeconds seconds; no retry was attempted."
    }
    throw
}
finally {
    if ($null -ne $request) { $request.Dispose() }
    if ($null -ne $content) { $content.Dispose() }
    $timeout.Dispose()
    $client.Dispose()
    $handler.Dispose()
}
