param(
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [string]$MesBaseUrl = 'http://127.0.0.1:5145/'
)
$ErrorActionPreference = 'Stop'
$releaseDirectory = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$applicationPath = Join-Path $releaseDirectory 'MesControlAgv.Wpf.exe'
if (!(Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw 'Published WPF executable not found.' }
$server = Invoke-RestMethod -Uri ($MesBaseUrl.TrimEnd('/') + '/api/shinelab/server/status') -TimeoutSec 5
if (!$server.enabled -or $server.payloadProfile -ne 'DcSeqSingleChannel') { throw 'Expected the existing DCSeq single-channel MES; no service will be started.' }
# Child-process settings only. Never change machine/user environment or own the field MES lifecycle.
$settings = @{
    WPF_RUNTIME_MODE = 'physical'
    WPF_MANAGE_LOCAL_SERVICES = 'false'
    WPF_MANAGE_LOCAL_MES = 'false'
    WPF_ENABLE_PHYSICAL_BATCH = 'false'
    WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL = 'false'
    WPF_START_PAGE = 'ShineLabControl'
    MES_BASE_URL = $MesBaseUrl
    ADAPTER_BASE_URL = 'http://127.0.0.1:15999/'
}
$previousSettings = @{}
try {
    foreach ($key in $settings.Keys) {
        $previousSettings[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    # This is the interactive control-center window explicitly requested for integration.
    Start-Process -FilePath $applicationPath -WorkingDirectory $releaseDirectory -WindowStyle Normal -PassThru |
        Select-Object Id, ProcessName
}
finally {
    foreach ($key in $previousSettings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousSettings[$key], 'Process')
    }
}
