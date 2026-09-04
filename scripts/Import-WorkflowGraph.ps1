[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TemplatePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$MesBaseUrl,

    [string]$Actor = 'admin',

    [switch]$Publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OptionalProperty {
    param(
        [AllowNull()] [object]$Value,
        [Parameter(Mandatory = $true)] [string]$Name
    )

    if ($null -eq $Value) { return $null }
    $property = @($Value.PSObject.Properties |
        Where-Object { $_.Name -ieq $Name } |
        Select-Object -First 1)
    if ($property.Count -eq 0) { return $null }
    return $property[0].Value
}

function Copy-StringMap {
    param([AllowNull()] [object]$Value)

    $result = [ordered]@{}
    if ($null -eq $Value) { return $result }
    foreach ($property in @($Value.PSObject.Properties)) {
        $result[$property.Name] = if ($null -eq $property.Value) {
            $null
        }
        else {
            [string]$property.Value
        }
    }
    return $result
}

function Save-NewJson {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [object]$Value
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite existing output '$Path'."
    }
    $json = $Value | ConvertTo-Json -Depth 60
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Save-NewText {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Text
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite existing output '$Path'."
    }
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-IntOrDefault {
    param(
        [AllowNull()] [object]$Value,
        [int]$Default = 0
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $Default
    }
    $parsed = 0
    if (-not [int]::TryParse([string]$Value, [ref]$parsed)) {
        throw "Expected an integer value, received '$Value'."
    }
    return $parsed
}

function Get-DoubleOrDefault {
    param(
        [AllowNull()] [object]$Value,
        [double]$Default = 0
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $Default
    }
    $parsed = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Expected a numeric value, received '$Value'."
    }
    return $parsed
}

function Get-BoolOrDefault {
    param(
        [AllowNull()] [object]$Value,
        [bool]$Default = $false
    )

    if ($null -eq $Value) { return $Default }
    if ($Value -is [bool]) { return [bool]$Value }
    $parsed = $false
    if (-not [bool]::TryParse([string]$Value, [ref]$parsed)) {
        throw "Expected a boolean value, received '$Value'."
    }
    return $parsed
}

function Invoke-MesPublishPost {
    param(
        [Parameter(Mandatory = $true)] [string]$Uri,
        [AllowNull()] [string]$Body,
        [Parameter(Mandatory = $true)] [string]$Stage,
        [Parameter(Mandatory = $true)] [string]$ResponseFile
    )

    $helper = Join-Path $PSScriptRoot 'Invoke-MesJsonUtf8.ps1'
    if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) {
        throw "UTF-8 HTTP helper was not found at '$helper'."
    }

    try {
        $arguments = @{
            Method = 'POST'
            Uri = $Uri
            TimeoutSeconds = 30
        }
        if ($null -ne $Body) { $arguments.Json = $Body }
        $raw = (& $helper @arguments | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($raw)) {
            throw "MES returned an empty response during '$Stage'."
        }
        Save-NewText -Path (Join-Path $OutputDirectory $ResponseFile) -Text $raw
        return $raw | ConvertFrom-Json
    }
    catch {
        $errorPath = Join-Path $OutputDirectory ("workflow-{0}-error.json" -f $Stage)
        if (-not (Test-Path -LiteralPath $errorPath)) {
            Save-NewJson -Path $errorPath -Value ([ordered]@{
                    schema = 'mes.physical-workflow-import-error/1.0'
                    stage = $Stage
                    uri = $Uri
                    attempts = 1
                    automaticRetry = $false
                    error = $_.Exception.Message
                    writesToDevicesAttempted = $false
                })
        }
        throw
    }
}

$templateAbsolute = [IO.Path]::GetFullPath($TemplatePath)
if (-not (Test-Path -LiteralPath $templateAbsolute -PathType Leaf)) {
    throw "Workflow graph template was not found: '$templateAbsolute'."
}

$outputAbsolute = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $outputAbsolute -PathType Container)) {
    New-Item -ItemType Directory -Path $outputAbsolute -Force | Out-Null
}

$actorNormalized = $Actor.Trim()
if ([string]::IsNullOrWhiteSpace($actorNormalized)) {
    throw 'Actor must not be empty.'
}

$mesBaseNormalized = $null
if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($MesBaseUrl)) {
        throw '-MesBaseUrl is required when -Publish is specified.'
    }
    $mesUri = $null
    $mesUriValid = [Uri]::TryCreate($MesBaseUrl.Trim(), [UriKind]::Absolute, [ref]$mesUri)
    if (-not $mesUriValid -or $mesUri.Scheme -notin @('http', 'https')) {
        throw "MesBaseUrl must be an absolute http/https address: '$MesBaseUrl'."
    }
    $mesBaseNormalized = $MesBaseUrl.TrimEnd('/')
}

$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$templateJson = [IO.File]::ReadAllText($templateAbsolute, $strictUtf8)
if ($templateJson -match '(?i)/Date\([^)]*\)/') {
    throw 'The graph input contains /Date(...)/. Use ISO-8601 strings for date values.'
}
$document = $templateJson | ConvertFrom-Json

$candidates = @()
$workflows = Get-OptionalProperty $document 'Workflows'
if ($null -ne $workflows) {
    $candidates = @($workflows)
}
else {
    $candidates = @($document)
}

$source = $null
foreach ($candidate in $candidates) {
    $candidateNodes = @(Get-OptionalProperty $candidate 'Nodes')
    $candidateEdges = @(Get-OptionalProperty $candidate 'Edges')
    if ($candidateNodes.Count -eq 9 -and $candidateEdges.Count -eq 8) {
        $source = $candidate
        break
    }
}
if ($null -eq $source) {
    throw 'An approved nine-node/eight-edge workflow graph was not found.'
}

$sourceNodes = @(Get-OptionalProperty $source 'Nodes')
$sourceEdges = @(Get-OptionalProperty $source 'Edges')
$expectedTypes = @(
    'core.start',
    'agv.move',
    'robot.execute-program',
    'agv.move',
    'robot.execute-program',
    'agv.move',
    'robot.execute-program',
    'agv.move',
    'core.end'
)

$nodeIds = @()
foreach ($node in $sourceNodes) {
    $id = [string](Get-OptionalProperty $node 'Id')
    if ([string]::IsNullOrWhiteSpace($id)) { throw 'Every graph node must have an id.' }
    $nodeIds += $id
}
if (@($nodeIds | Select-Object -Unique).Count -ne $nodeIds.Count) {
    throw 'Workflow graph node ids must be unique.'
}

$orderedNodes = @($sourceNodes)
$hasExplicitOrder = $true
foreach ($node in $sourceNodes) {
    $orderValue = Get-OptionalProperty $node 'Order'
    $ignored = 0
    if ($null -eq $orderValue -or -not [int]::TryParse([string]$orderValue, [ref]$ignored)) {
        $hasExplicitOrder = $false
        break
    }
}
if ($hasExplicitOrder) {
    $orderedNodes = @($sourceNodes | Sort-Object { Get-IntOrDefault (Get-OptionalProperty $_ 'Order') })
}

$nodeMap = @{}
foreach ($node in $sourceNodes) {
    $nodeMap[[string](Get-OptionalProperty $node 'Id')] = [Guid]::NewGuid()
}

$typeMap = @{
    'core.start' = 0
    'agv.move' = 1
    'robot.execute-program' = 8
    'core.end' = 5
}
for ($index = 0; $index -lt $orderedNodes.Count; $index++) {
    $typeId = ([string](Get-OptionalProperty $orderedNodes[$index] 'NodeTypeId')).Trim().ToLowerInvariant()
    if (-not $typeMap.ContainsKey($typeId)) {
        throw "Unsupported node type '$typeId' in the approved material template."
    }
    if ($typeId -ne $expectedTypes[$index]) {
        throw "Approved material template node $($index + 1) must be '$($expectedTypes[$index])', got '$typeId'."
    }
}

$edgeMap = @{}
$edgeIds = @()
foreach ($edge in $sourceEdges) {
    $edgeId = [string](Get-OptionalProperty $edge 'Id')
    if ([string]::IsNullOrWhiteSpace($edgeId)) { throw 'Every graph edge must have an id.' }
    $edgeIds += $edgeId
    $edgeMap[$edgeId] = [Guid]::NewGuid()
    $sourceId = [string](Get-OptionalProperty $edge 'SourceNodeId')
    $targetId = [string](Get-OptionalProperty $edge 'TargetNodeId')
    if (-not $nodeMap.ContainsKey($sourceId) -or -not $nodeMap.ContainsKey($targetId)) {
        throw "Edge '$edgeId' references an unknown node."
    }
}
if (@($edgeIds | Select-Object -Unique).Count -ne $edgeIds.Count) {
    throw 'Workflow graph edge ids must be unique.'
}

$layouts = @{}
foreach ($layout in @(Get-OptionalProperty $source 'Layouts')) {
    $layoutNodeId = [string](Get-OptionalProperty $layout 'NodeId')
    if ($nodeMap.ContainsKey($layoutNodeId)) { $layouts[$layoutNodeId] = $layout }
}

$edgesOut = [System.Collections.Generic.List[object]]::new()
foreach ($edge in $sourceEdges) {
    $edgeId = [string](Get-OptionalProperty $edge 'Id')
    $sourceId = [string](Get-OptionalProperty $edge 'SourceNodeId')
    $targetId = [string](Get-OptionalProperty $edge 'TargetNodeId')
    $metadata = Copy-StringMap (Get-OptionalProperty $edge 'Metadata')
    $edgesOut.Add([ordered]@{
            id = $edgeMap[$edgeId]
            sourceNodeId = $nodeMap[$sourceId]
            sourcePort = [string](Get-OptionalProperty $edge 'SourcePort')
            targetNodeId = $nodeMap[$targetId]
            targetPort = [string](Get-OptionalProperty $edge 'TargetPort')
            kind = Get-IntOrDefault (Get-OptionalProperty $edge 'Kind')
            condition = Get-OptionalProperty $edge 'Condition'
            conditionExpression = Get-OptionalProperty $edge 'ConditionExpression'
            priority = Get-IntOrDefault (Get-OptionalProperty $edge 'Priority')
            metadata = $metadata
        })
}

$nodesOut = [System.Collections.Generic.List[object]]::new()
$order = 1
foreach ($node in $orderedNodes) {
    $originalId = [string](Get-OptionalProperty $node 'Id')
    $typeId = ([string](Get-OptionalProperty $node 'NodeTypeId')).Trim().ToLowerInvariant()
    $configuration = Copy-StringMap (Get-OptionalProperty $node 'Configuration')
    $targetStation = if ($configuration.Contains('$targetStation')) {
        $configuration['$targetStation']
    }
    else {
        $null
    }
    if ([string]::IsNullOrWhiteSpace([string]$targetStation)) {
        $targetStation = Get-OptionalProperty $node 'TargetStation'
    }

    $nextNodeIds = @($sourceEdges |
        Where-Object {
            [string](Get-OptionalProperty $_ 'SourceNodeId') -eq $originalId -and
            [string](Get-OptionalProperty $_ 'SourcePort') -eq 'success' -and
            (Get-IntOrDefault (Get-OptionalProperty $_ 'Kind')) -eq 0
        } |
        Sort-Object { Get-IntOrDefault (Get-OptionalProperty $_ 'Priority') } |
        ForEach-Object { $nodeMap[[string](Get-OptionalProperty $_ 'TargetNodeId')] })

    $portsOut = [System.Collections.Generic.List[object]]::new()
    $rawPorts = Get-OptionalProperty $node 'Ports'
    if ($null -ne $rawPorts) {
        foreach ($port in @($rawPorts)) {
            $edgeKindValue = Get-OptionalProperty $port 'EdgeKind'
            $portsOut.Add([ordered]@{
                    key = [string](Get-OptionalProperty $port 'Key')
                    displayName = [string](Get-OptionalProperty $port 'DisplayName')
                    direction = Get-IntOrDefault (Get-OptionalProperty $port 'Direction')
                    dataType = [string](Get-OptionalProperty $port 'DataType')
                    cardinality = Get-IntOrDefault (Get-OptionalProperty $port 'Cardinality')
                    edgeKind = if ($null -eq $edgeKindValue) { $null } else { Get-IntOrDefault $edgeKindValue }
                })
        }
    }

    $parametersOut = [System.Collections.Generic.List[object]]::new()
    $rawParameters = Get-OptionalProperty $node 'Parameters'
    if ($null -ne $rawParameters) {
        foreach ($parameter in @($rawParameters)) {
            $required = Get-OptionalProperty $parameter 'IsRequired'
            $parameterValue = Get-OptionalProperty $parameter 'Value'
            $parametersOut.Add([ordered]@{
                    name = [string](Get-OptionalProperty $parameter 'Name')
                    value = if ($null -eq $parameterValue) { $null } else { [string]$parameterValue }
                    dataType = [string](Get-OptionalProperty $parameter 'DataType')
                    isRequired = Get-BoolOrDefault $required
                })
        }
    }

    $layout = if ($layouts.ContainsKey($originalId)) { $layouts[$originalId] } else { $null }
    $nodesOut.Add([ordered]@{
            id = $nodeMap[$originalId]
            type = $typeMap[$typeId]
            nodeTypeId = $typeId
            schemaVersion = if ([string]::IsNullOrWhiteSpace([string](Get-OptionalProperty $node 'SchemaVersion'))) { '1.0' } else { [string](Get-OptionalProperty $node 'SchemaVersion') }
            name = [string](Get-OptionalProperty $node 'Name')
            description = [string](Get-OptionalProperty $node 'Description')
            targetStation = if ($null -eq $targetStation) { $null } else { [string]$targetStation }
            x = if ($null -eq $layout) { Get-DoubleOrDefault (Get-OptionalProperty $node 'X') } else { Get-DoubleOrDefault (Get-OptionalProperty $layout 'X') }
            y = if ($null -eq $layout) { Get-DoubleOrDefault (Get-OptionalProperty $node 'Y') } else { Get-DoubleOrDefault (Get-OptionalProperty $layout 'Y') }
            order = $order
            parameters = @($parametersOut.ToArray())
            nextNodeIds = $nextNodeIds
            ports = @($portsOut.ToArray())
            configuration = $configuration
        })
    $order++
}

$layoutsOut = [System.Collections.Generic.List[object]]::new()
foreach ($layout in @(Get-OptionalProperty $source 'Layouts')) {
    $originalId = [string](Get-OptionalProperty $layout 'NodeId')
    if (-not $nodeMap.ContainsKey($originalId)) { continue }
    $layoutsOut.Add([ordered]@{
            nodeId = $nodeMap[$originalId]
            x = Get-DoubleOrDefault (Get-OptionalProperty $layout 'X')
            y = Get-DoubleOrDefault (Get-OptionalProperty $layout 'Y')
            width = Get-DoubleOrDefault (Get-OptionalProperty $layout 'Width') 200
            height = Get-DoubleOrDefault (Get-OptionalProperty $layout 'Height') 120
        })
}

$viewport = Get-OptionalProperty $source 'Viewport'
$workflowId = [Guid]::NewGuid()
$definition = [ordered]@{
    id = $workflowId
    schemaVersion = 3
    name = [string](Get-OptionalProperty $source 'Name')
    description = [string](Get-OptionalProperty $source 'Description')
    isPreset = Get-BoolOrDefault (Get-OptionalProperty $source 'IsPreset') $true
    nodes = @($nodesOut.ToArray())
    edges = @($edgesOut.ToArray())
    layouts = @($layoutsOut.ToArray())
    viewport = [ordered]@{
        x = Get-DoubleOrDefault (Get-OptionalProperty $viewport 'X')
        y = Get-DoubleOrDefault (Get-OptionalProperty $viewport 'Y')
        zoom = Get-DoubleOrDefault (Get-OptionalProperty $viewport 'Zoom') 1
    }
}

$definitionPath = Join-Path $outputAbsolute 'workflow-definition-request.json'
Save-NewJson -Path $definitionPath -Value $definition

$summary = [ordered]@{
    schema = 'mes.physical-workflow-import/1.0'
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    workflowId = $workflowId.ToString()
    name = $definition.name
    nodes = $nodesOut.Count
    edges = $edgesOut.Count
    nodeTypes = @($nodesOut.ToArray() | ForEach-Object { $_.nodeTypeId })
    publicationAttempted = [bool]$Publish
    writesToDevicesAttempted = $false
    automaticRetry = $false
}
Save-NewJson -Path (Join-Path $outputAbsolute 'workflow-import-summary.json') -Value $summary

if ($Publish) {
    $actorQuery = [Uri]::EscapeDataString($actorNormalized)
    $draft = Invoke-MesPublishPost `
        -Uri "$mesBaseNormalized/api/workflows?actor=$actorQuery" `
        -Body ($definition | ConvertTo-Json -Depth 60) `
        -Stage 'draft' `
        -ResponseFile 'workflow-draft-response.json'
    $version = Get-IntOrDefault (Get-OptionalProperty $draft 'Version')
    if ($version -lt 1) { throw "MES returned an invalid workflow version '$version'." }

    $validation = Invoke-MesPublishPost `
        -Uri "$mesBaseNormalized/api/workflows/$workflowId/versions/$version/validate" `
        -Body $null `
        -Stage 'validate' `
        -ResponseFile 'workflow-validation-response.json'
    if (-not [bool](Get-OptionalProperty $validation 'IsValid')) {
        throw 'MES rejected the imported workflow during validation; no publish request was sent.'
    }

    $published = Invoke-MesPublishPost `
        -Uri "$mesBaseNormalized/api/workflows/$workflowId/versions/$version/publish?actor=$actorQuery" `
        -Body $null `
        -Stage 'publish' `
        -ResponseFile 'workflow-publish-response.json'
    $publicationSummary = [ordered]@{
        schema = 'mes.physical-workflow-publication/1.0'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        workflowId = $workflowId.ToString()
        version = $version
        actor = $actorNormalized
        status = Get-OptionalProperty $published 'Status'
        publishStatus = Get-OptionalProperty $published 'PublishStatus'
        writesToDevicesAttempted = $false
        automaticRetry = $false
    }
    Save-NewJson -Path (Join-Path $outputAbsolute 'workflow-publication-summary.json') -Value $publicationSummary
}

Write-Output ($summary | ConvertTo-Json -Depth 20)
