param(
    [string]$RunId = ("wpf-ui-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [ValidateSet('Debug', 'Release')]
    [string]$ServiceConfiguration = 'Release',
    [string]$SimulatorUrl = 'http://localhost:5611',
    [string]$AdapterUrl = 'http://localhost:5612',
    [string]$MesUrl = 'http://localhost:5613',
    [string]$MesDatabasePath,
    [string]$AdapterDatabasePath,
    [string]$StatePath,
    [int]$SourceStationCode = 2,
    [int]$TargetStationCode = 3,
    [int]$Priority = 7,
    [string]$ExternalId,
    [string]$Description = 'Windows UI Automation offline dynamic-route smoke',
    [string]$OperatorName = 'wpf-uia-operator',
    [int]$TimeoutSeconds = 45,
    [switch]$KeepServices,
    [switch]$DumpTree
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$wpfConfiguration = 'Debug'
$startedServices = $false
$wpfProcess = $null
$window = $null

function Assert-SafeRunId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z0-9._-]+$') {
        throw 'RunId must contain letters, digits, dot, underscore, and hyphen only.'
    }
}

function Invoke-PowerShellScript {
    param(
        [string]$Path,
        [string[]]$Arguments
    )

    $powershell = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source
    if ([string]::IsNullOrWhiteSpace($powershell)) {
        throw 'powershell.exe is required to launch the isolated local services.'
    }

    & $powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "PowerShell script '$Path' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Json {
    param(
        [ValidateSet('Get', 'Post')]
        [string]$Method = 'Get',
        [string]$Uri,
        [AllowNull()][object]$Body
    )

    if ($Method -eq 'Post') {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method Post -Uri $Uri -TimeoutSec 5
        }

        return Invoke-RestMethod -Method Post -Uri $Uri -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 8) -TimeoutSec 5
    }

    return Invoke-RestMethod -Method Get -Uri $Uri -TimeoutSec 5
}

function Get-JsonCollectionItems {
    param([AllowNull()][object]$Response)

    if ($null -eq $Response) { return @() }

    foreach ($propertyName in @('value', 'items', 'data')) {
        $property = $Response.PSObject.Properties[$propertyName]
        if ($null -ne $property) {
            return @(Get-JsonCollectionItems $property.Value)
        }
    }

    if ($Response -is [System.Array]) {
        return @($Response | ForEach-Object { Get-JsonCollectionItems $_ })
    }

    return @($Response)
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage,
        [int]$Timeout = $TimeoutSeconds,
        [int]$DelayMilliseconds = 250
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        try {
            $value = & $Condition
            if ($value) { return $value }
        }
        catch {
        }

        Start-Sleep -Milliseconds $DelayMilliseconds
    } while ([DateTime]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Get-TaskDetail {
    param([Guid]$TaskId)

    Invoke-Json -Uri "$($MesUrl.TrimEnd('/'))/api/tasks/$TaskId"
}

function Wait-TaskStatus {
    param(
        [Guid]$TaskId,
        [string]$ExpectedStatus
    )

    Wait-Until -FailureMessage "Task $TaskId did not reach '$ExpectedStatus'." -Condition {
        $detail = Get-TaskDetail $TaskId
        if ($detail.task.status -eq $ExpectedStatus) { return $detail }
        return $null
    }
}

function Get-VisibleElements {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $result = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements.Item($index)
        try {
            if (-not $element.Current.IsOffscreen) { $result.Add($element) }
        }
        catch {
        }
    }

    @($result)
}

function Get-Elements {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $result = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $result.Add($elements.Item($index))
    }

    @($result)
}

function Get-ElementName {
    param([System.Windows.Automation.AutomationElement]$Element)

    try { return [string]$Element.Current.Name } catch { return '' }
}

function Get-ElementBounds {
    param([System.Windows.Automation.AutomationElement]$Element)

    try { return $Element.Current.BoundingRectangle } catch { return [System.Windows.Rect]::Empty }
}

function Test-UiElementText {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Pattern
    )

    if ((Get-ElementName $Element) -like "*$Pattern*") { return $true }
    foreach ($text in @(Get-Elements $Element ([System.Windows.Automation.ControlType]::Text))) {
        if ((Get-ElementName $text) -like "*$Pattern*") { return $true }
    }

    return $false
}

function Find-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [switch]$Required
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Required -and $null -eq $element) {
        throw "UIA element with AutomationId '$AutomationId' was not found. Use -DumpTree to inspect the current tree."
    }

    return $element
}

function Find-ElementByText {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType,
        [string[]]$Patterns,
        [switch]$Required
    )

    $elements = Get-VisibleElements $Root $ControlType
    foreach ($pattern in $Patterns) {
        $exact = @($elements | Where-Object { (Get-ElementName $_) -eq $pattern })
        if ($exact.Count -gt 0) { return $exact[0] }
    }

    foreach ($pattern in $Patterns) {
        $contains = @($elements | Where-Object { [string](Get-ElementName $_) -like "*$pattern*" })
        if ($contains.Count -gt 0) { return $contains[0] }
    }

    if ($Required) {
        throw "UIA element '$($Patterns -join ', ')' was not found. Use -DumpTree to inspect the current tree."
    }

    return $null
}

function Find-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Patterns,
        [int]$FallbackIndex = -1,
        [double]$MinimumTop = 0,
        [double]$MaximumTop = [double]::PositiveInfinity
    )

    $button = Find-ElementByText $Root ([System.Windows.Automation.ControlType]::Button) $Patterns
    if ($null -ne $button) { return $button }

    $buttons = @(
        Get-VisibleElements $Root ([System.Windows.Automation.ControlType]::Button) |
            Where-Object {
                $bounds = Get-ElementBounds $_
                $bounds.Top -ge $MinimumTop -and $bounds.Top -le $MaximumTop -and $_.Current.IsEnabled
            } |
            Sort-Object @{ Expression = { (Get-ElementBounds $_).Top } }, @{ Expression = { (Get-ElementBounds $_).Left } }
    )
    if ($FallbackIndex -ge 0 -and $FallbackIndex -lt $buttons.Count) { return $buttons[$FallbackIndex] }

    throw "UIA button '$($Patterns -join ', ')' was not found. Current visible button names: $(@($buttons | ForEach-Object { Get-ElementName $_ }) -join '; ')"
}

function Invoke-UiElement {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Description
    )

    if ($null -eq $Element) { throw "Cannot invoke missing UIA element '$Description'." }
    if (-not $Element.Current.IsEnabled) { throw "UIA element '$Description' is disabled." }

    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Get-UiElementEnabledDiagnostic {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $element = Find-ElementByAutomationId $Root $AutomationId
    if ($null -eq $element) {
        return "${AutomationId}=<not found>"
    }

    try {
        return "{0}: Name='{1}', IsEnabled={2}" -f $AutomationId, [string]$element.Current.Name, [bool]$element.Current.IsEnabled
    }
    catch {
        return "${AutomationId}=<unavailable: $($_.Exception.Message)>"
    }
}

function Get-AgvCommandGateDiagnostic {
    param([System.Windows.Automation.AutomationElement]$Root)

    $gate = Find-ElementByAutomationId $Root 'AgvCommandGateStatusText'
    if ($null -eq $gate) {
        $gateText = 'AgvCommandGateStatusText=<not found>'
    }
    else {
        try {
            $gateText = "AgvCommandGateStatusText='{0}'" -f [string]$gate.Current.Name
        }
        catch {
            $gateText = "AgvCommandGateStatusText=<unavailable: $($_.Exception.Message)>"
        }
    }

    $buttonText = @(
        Get-UiElementEnabledDiagnostic $Root 'PauseAgvButton'
        Get-UiElementEnabledDiagnostic $Root 'ResumeAgvButton'
        Get-UiElementEnabledDiagnostic $Root 'CancelAgvButton'
    ) -join '; '

    return "$gateText; $buttonText"
}

function Wait-UiElementEnabled {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Description
    )

    try {
        Wait-Until -FailureMessage "WPF UIA element '$Description' ($AutomationId) did not become enabled." -Condition {
            $element = Find-ElementByAutomationId $Root $AutomationId
            if ($null -ne $element -and $element.Current.IsEnabled) { return $element }
            return $null
        }
    }
    catch {
        $diagnostic = Get-AgvCommandGateDiagnostic $Root
        throw "WPF UIA element '$Description' ($AutomationId) did not become enabled. AGV command gate: $diagnostic"
    }
}

function Assert-UiElementNameContains {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Text,
        [string]$Description
    )

    Wait-Until -FailureMessage "WPF UI did not show $Description ('$Text') on '$AutomationId'." -Condition {
        $element = Find-ElementByAutomationId $Root $AutomationId
        if ($null -ne $element -and [string]$element.Current.Name -like "*$Text*") { return $element }
        return $null
    } | Out-Null
}

function Set-UiText {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value,
        [string]$Description
    )

    if ($null -eq $Element) { throw "Cannot set missing UIA text box '$Description'." }
    $Element.SetFocus()
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

function Select-UiComboItem {
    param(
        [System.Windows.Automation.AutomationElement]$Combo,
        [string[]]$Patterns,
        [string]$Description
    )

    if ($null -eq $Combo) { throw "Cannot select missing UIA combo box '$Description'." }
    $Combo.SetFocus()
    $expandPattern = $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Expand()
    Start-Sleep -Milliseconds 200

    $listItemType = [System.Windows.Automation.ControlType]::ListItem
    $items = @(Get-VisibleElements $Combo $listItemType)
    if ($items.Count -eq 0) {
        $items = @(Get-VisibleElements ([System.Windows.Automation.AutomationElement]::RootElement) $listItemType)
    }
    $realizedItems = @(Get-Elements $Combo $listItemType)
    if ($realizedItems.Count -gt 0) {
        $items = @($items + $realizedItems | Select-Object -Unique)
    }

    $item = $null
    foreach ($pattern in $Patterns) {
        $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $pattern)
        $item = $Combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
        if ($null -ne $item) { break }
    }
    foreach ($pattern in $Patterns) {
        if ($null -ne $item) { break }
        $item = @($items | Where-Object { Test-UiElementText $_ $pattern } | Select-Object -First 1)
        if ($item.Count -gt 0) { $item = $item[0]; break }
        $item = $null
    }
    if ($null -eq $item) {
        ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Collapse()
        throw "UIA combo item '$($Patterns -join ', ')' was not found for '$Description'. Use -DumpTree to inspect list items."
    }

    try {
        $selection = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        ([System.Windows.Automation.SelectionItemPattern]$selection).Select()
    }
    catch {
        $invoke = $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        ([System.Windows.Automation.InvokePattern]$invoke).Invoke()
    }
    Start-Sleep -Milliseconds 150
    ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Collapse()
}

function Get-WindowText {
    param([System.Windows.Automation.AutomationElement]$Root)

    $texts = Get-VisibleElements $Root ([System.Windows.Automation.ControlType]::Text)
    @($texts | ForEach-Object { Get-ElementName $_ })
}

function Assert-UiContains {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Text,
        [string]$Description
    )

    Wait-Until -FailureMessage "WPF UI did not show $Description ('$Text')." -Condition {
        @(Get-WindowText $Root | Where-Object { [string]$_ -like "*$Text*" }).Count -gt 0
    } | Out-Null
}

function Assert-UiWindowMaximized {
    param([System.Windows.Automation.AutomationElement]$Window)

    try {
        $pattern = [System.Windows.Automation.WindowPattern]$Window.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)
        $visualState = $pattern.Current.WindowVisualState
    }
    catch {
        throw "UI Automation could not read the WPF window visual state: $($_.Exception.Message)"
    }

    if ($visualState -ne [System.Windows.Automation.WindowVisualState]::Maximized) {
        throw "WPF main window visual state is '$visualState'; expected 'Maximized'."
    }
}

function Write-UiTree {
    param([System.Windows.Automation.AutomationElement]$Root)

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    function Write-Node {
        param([System.Windows.Automation.AutomationElement]$Node, [int]$Depth)
        if ($null -eq $Node) { return }
        try {
            $current = $Node.Current
            $indent = (' ' * ($Depth * 2))
            $bounds = $current.BoundingRectangle
            Write-Host ("{0}{1} name='{2}' automationId='{3}' class='{4}' enabled={5} offscreen={6} bounds=({7:0},{8:0},{9:0},{10:0})" -f `
                $indent, $current.ControlType.ProgrammaticName, $current.Name, $current.AutomationId, $current.ClassName,
                $current.IsEnabled, $current.IsOffscreen, $bounds.Left, $bounds.Top, $bounds.Width, $bounds.Height)
            $child = $walker.GetFirstChild($Node)
            while ($null -ne $child) {
                Write-Node $child ($Depth + 1)
                $child = $walker.GetNextSibling($child)
            }
        }
        catch {
        }
    }

    Write-Node $Root 0
}

function Get-TaskFormControls {
    param([System.Windows.Automation.AutomationElement]$Root)

    [pscustomobject]@{
        SourceCombo = Find-ElementByAutomationId $Root 'TaskSourceStationCombo' -Required
        TargetCombo = Find-ElementByAutomationId $Root 'TaskTargetStationCombo' -Required
        PriorityTextBox = Find-ElementByAutomationId $Root 'TaskPriorityTextBox' -Required
        ExternalIdTextBox = Find-ElementByAutomationId $Root 'TaskExternalIdTextBox' -Required
        OperatorTextBox = Find-ElementByAutomationId $Root 'TaskOperatorTextBox' -Required
        DescriptionTextBox = Find-ElementByAutomationId $Root 'TaskDescriptionTextBox' -Required
    }
}

function Start-WpfProcess {
    param([string]$DllPath)

    $dotnet = (Get-Command dotnet.exe).Source
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $dotnet
    $psi.Arguments = '"{0}"' -f $DllPath
    $psi.WorkingDirectory = Join-Path $repoRoot 'src\MesControlAgv.Wpf'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    $psi.Environment['MES_BASE_URL'] = "$($MesUrl.TrimEnd('/'))/"
    $psi.Environment['SIMULATOR_BASE_URL'] = "$($SimulatorUrl.TrimEnd('/'))/"
    $psi.Environment['WPF_RUNTIME_MODE'] = 'simulator'

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) { throw 'The WPF process could not be started.' }
    return $process
}

function Stop-WpfProcess {
    param([AllowNull()][System.Diagnostics.Process]$Process)

    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            $Process.CloseMainWindow() | Out-Null
            if (-not $Process.WaitForExit(5000)) {
                $Process.Kill()
                $Process.WaitForExit()
            }
        }
    }
    catch {
        try {
            if (-not $Process.HasExited) {
                $Process.Kill()
                $Process.WaitForExit()
            }
        }
        catch { }
    }
}

Assert-SafeRunId $RunId
if ($TimeoutSeconds -lt 10) { throw 'TimeoutSeconds must be at least 10.' }
if ($Priority -lt 0) { throw 'Priority must be zero or greater.' }
if ($SourceStationCode -eq $TargetStationCode) { throw 'Source and target station codes must differ.' }
if ($SourceStationCode -eq 2 -and $TargetStationCode -eq 4) {
    throw 'The UI smoke must use a non-default route. Choose another target (for example 2 -> 3).'
}

$runRoot = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-$RunId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($MesDatabasePath)) { $MesDatabasePath = Join-Path $runRoot 'mes.db' }
if ([string]::IsNullOrWhiteSpace($AdapterDatabasePath)) { $AdapterDatabasePath = Join-Path $runRoot 'adapter.db' }
if ([string]::IsNullOrWhiteSpace($StatePath)) { $StatePath = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-local-$RunId-pids.json" }
$MesDatabasePath = [IO.Path]::GetFullPath($MesDatabasePath)
$AdapterDatabasePath = [IO.Path]::GetFullPath($AdapterDatabasePath)
$StatePath = [IO.Path]::GetFullPath($StatePath)
if (Test-Path -LiteralPath $StatePath) { throw "State file already exists at '$StatePath'." }
if ([string]::IsNullOrWhiteSpace($ExternalId)) { $ExternalId = "WPF-UIA-$RunId" }

$runScript = Join-Path $PSScriptRoot 'run-local.ps1'
$stopScript = Join-Path $PSScriptRoot 'stop-local.ps1'
$wpfDll = Join-Path $repoRoot "src\MesControlAgv.Wpf\bin\$wpfConfiguration\net8.0-windows\MesControlAgv.Wpf.dll"
if (-not (Test-Path -LiteralPath $runScript -PathType Leaf)) { throw "Local runner was not found at '$runScript'." }
if (-not (Test-Path -LiteralPath $stopScript -PathType Leaf)) { throw "Local stopper was not found at '$stopScript'." }
if (-not (Test-Path -LiteralPath $wpfDll -PathType Leaf)) {
    throw "Debug WPF DLL was not found at '$wpfDll'. Build it before running the UI smoke."
}

try {
    Invoke-PowerShellScript $runScript @(
        '-Configuration', $ServiceConfiguration,
        '-RunId', $RunId,
        '-StatePath', $StatePath,
        '-SimulatorUrl', $SimulatorUrl,
        '-AdapterUrl', $AdapterUrl,
        '-MesUrl', $MesUrl,
        '-MesDatabasePath', $MesDatabasePath,
        '-AdapterDatabasePath', $AdapterDatabasePath,
        '-RequireIsolatedStores'
    )
    $startedServices = $true

    $stations = @(Get-JsonCollectionItems (Invoke-Json -Uri "$($MesUrl.TrimEnd('/'))/api/stations")) | Where-Object { $_.enabled }
    $source = $stations | Where-Object { [int]$_.code -eq $SourceStationCode } | Select-Object -First 1
    $target = $stations | Where-Object { [int]$_.code -eq $TargetStationCode } | Select-Object -First 1
    if ($null -eq $source -or $null -eq $target) {
        throw "Configured source/target station codes $SourceStationCode -> $TargetStationCode are not both enabled in MES."
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $wpfProcess = Start-WpfProcess $wpfDll
    $windowHandle = Wait-Until -FailureMessage 'Debug WPF did not expose a main window. Run this script from an interactive Windows desktop.' -Condition {
        $wpfProcess.Refresh()
        if ($wpfProcess.HasExited) { throw "WPF exited with code $($wpfProcess.ExitCode)." }
        $handle = $wpfProcess.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) { return $handle }
        return $null
    }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$windowHandle)
    if ($null -eq $window) { throw 'UI Automation could not attach to the WPF main window.' }
    Assert-UiWindowMaximized $window
    if ($DumpTree) { Write-UiTree $window }

    $form = Get-TaskFormControls $window
    Select-UiComboItem $form.SourceCombo @([string]$source.agvStationId, [string]$source.name) 'source station'
    Select-UiComboItem $form.TargetCombo @([string]$target.agvStationId, [string]$target.name) 'target station'
    Set-UiText $form.PriorityTextBox ([string]$Priority) 'task priority'
    Set-UiText $form.ExternalIdTextBox $ExternalId 'external id'
    Set-UiText $form.OperatorTextBox $OperatorName 'operator name'
    Set-UiText $form.DescriptionTextBox $Description 'task description'

    $planButton = Wait-UiElementEnabled $window 'PlanRouteButton' 'preview route'
    Invoke-UiElement $planButton 'preview route'
    Assert-UiContains $window ([string]$target.agvStationId) 'the configured route preview'

    $createButton = Wait-UiElementEnabled $window 'CreateTaskButton' 'create task'
    Invoke-UiElement $createButton 'create task'
    $created = Wait-Until -FailureMessage "MES did not create WPF task '$ExternalId'." -Condition {
        $utcDate = [DateTime]::UtcNow.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        @(Get-JsonCollectionItems (Invoke-Json -Uri "$($MesUrl.TrimEnd('/'))/api/tasks?date=$utcDate") |
            Where-Object { $_.externalId -eq $ExternalId } |
            Select-Object -First 1)
    }
    $taskId = [Guid]$created.id
    if ($created.sourceStationCode -ne $SourceStationCode -or $created.targetStationCode -ne $TargetStationCode) {
        throw "WPF created route $($created.sourceStationCode) -> $($created.targetStationCode), expected $SourceStationCode -> $TargetStationCode."
    }
    if ($created.priority -ne $Priority -or $created.description -ne $Description) {
        throw 'WPF did not persist the configured priority/description metadata.'
    }
    Assert-UiContains $window ([string]$taskId) 'the created task id'

    $dispatchButton = Wait-UiElementEnabled $window 'DispatchTaskButton' 'dispatch task'
    Invoke-UiElement $dispatchButton 'dispatch task'
    $dispatched = Wait-TaskStatus $taskId 'MovingToPickup'
    if ([string]::IsNullOrWhiteSpace([string]$dispatched.task.activeAgvId) -or [string]::IsNullOrWhiteSpace([string]$dispatched.task.activeDeviceTaskId)) {
        throw 'WPF dispatch did not expose active AGV/device-task correlation.'
    }
    Assert-UiContains $window 'MovingToPickup' 'pickup execution status'

    $agvTab = Find-ElementByAutomationId $window 'AgvCommunicationTab' -Required
    $tabSelection = $agvTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    ([System.Windows.Automation.SelectionItemPattern]$tabSelection).Select()
    Start-Sleep -Milliseconds 250
    $pauseButton = Wait-UiElementEnabled $window 'PauseAgvButton' 'pause AGV task'
    Invoke-UiElement $pauseButton 'pause AGV task'
    Wait-TaskStatus $taskId 'Paused' | Out-Null
    Assert-UiContains $window 'Paused' 'paused task status'
    $resumeButton = Wait-UiElementEnabled $window 'ResumeAgvButton' 'resume AGV task'
    Invoke-UiElement $resumeButton 'resume AGV task'
    Wait-TaskStatus $taskId 'MovingToPickup' | Out-Null
    Assert-UiContains $window 'MovingToPickup' 'resumed pickup execution status'

    $taskTab = Find-ElementByAutomationId $window 'TaskMonitorTab' -Required
    $taskSelection = $taskTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    ([System.Windows.Automation.SelectionItemPattern]$taskSelection).Select()
    Start-Sleep -Milliseconds 250
    $arriveButton = Wait-UiElementEnabled $window 'SimulateArrivalButton' 'pickup arrival'
    Invoke-UiElement $arriveButton 'pickup arrival'
    Wait-TaskStatus $taskId 'WaitingPickupConfirmation' | Out-Null
    Assert-UiElementNameContains $window 'TaskStatusText' 'WaitingPickupConfirmation' 'pickup confirmation state'
    $pickupButton = Wait-UiElementEnabled $window 'ConfirmPickupButton' 'confirm pickup'
    Invoke-UiElement $pickupButton 'confirm pickup'
    Wait-TaskStatus $taskId 'MovingToDropoff' | Out-Null
    Assert-UiElementNameContains $window 'TaskStatusText' 'MovingToDropoff' 'dropoff execution status'

    $arriveButton = Wait-UiElementEnabled $window 'SimulateArrivalButton' 'dropoff arrival'
    Invoke-UiElement $arriveButton 'dropoff arrival'
    Wait-TaskStatus $taskId 'WaitingDropoffConfirmation' | Out-Null
    Assert-UiElementNameContains $window 'TaskStatusText' 'WaitingDropoffConfirmation' 'dropoff confirmation state'
    $dropoffButton = Wait-UiElementEnabled $window 'ConfirmDropoffButton' 'confirm dropoff'
    Invoke-UiElement $dropoffButton 'confirm dropoff'
    $completed = Wait-TaskStatus $taskId 'Completed'
    $events = @((Get-TaskDetail $taskId).events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'DispatchRequested', 'PauseRequested', 'ResumeRequested', 'PickupArrived', 'PickupConfirmed', 'DropoffArrived', 'DropoffConfirmed')) {
        if ($events -notcontains $requiredEvent) { throw "WPF UI flow is missing audit event '$requiredEvent'." }
    }
    $fleet = @(Get-JsonCollectionItems (Invoke-Json -Uri "$($MesUrl.TrimEnd('/'))/api/agvs/fleet/status"))
    if (@($fleet | Where-Object { $null -ne $_.activeTask }).Count -gt 0) { throw 'Completed WPF task remained active in MES fleet status.' }
    $adapterFleet = @(Get-JsonCollectionItems (Invoke-Json -Uri "$($AdapterUrl.TrimEnd('/'))/agvs"))
    if (@($adapterFleet | Where-Object { $null -ne $_.currentTaskId -and -not [string]::IsNullOrWhiteSpace([string]$_.currentTaskId) }).Count -gt 0) {
        throw 'Completed WPF task remained active in Adapter fleet status.'
    }
    Assert-UiElementNameContains $window 'TaskStatusText' 'Completed' 'the final completed status'
    Write-Host "WPF UIA offline verification passed for task $taskId ($SourceStationCode -> $TargetStationCode, AGV $($completed.task.activeAgvId))."
    Write-Host "RunId: $RunId; services: $SimulatorUrl, $AdapterUrl, $MesUrl; audit events: $($events -join ', ')."
}
catch {
    if ($null -ne $window) {
        Write-Host "WPF UIA verification failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-UiTree $window
    }
    throw
}
finally {
    Stop-WpfProcess $wpfProcess
    if ($startedServices -and -not $KeepServices) {
        try {
            Invoke-PowerShellScript $stopScript @('-RunId', $RunId, '-StatePath', $StatePath)
        }
        catch {
            Write-Warning "Unable to stop local service run '$RunId': $($_.Exception.Message)"
        }
    }
}
