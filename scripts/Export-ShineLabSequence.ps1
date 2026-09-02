<#
.SYNOPSIS
    只读验证：导出当前 ShineLab 样品任务序列到临时 CSV，并与期望 CSV 做结构化比对。

.DESCRIPTION
    该脚本只执行“导出CSV”这一只读动作，绝不导入、绝不点击“运行”、不写串口/寄存器。
    流程：
      1. 校验期望 CSV（表头与任务数）。
      2. 前台激活 ShineLab，切到分析控制，右键任务表。
      3. 选择右键菜单第一项“导出CSV”（已验证菜单顺序：导出CSV / 从CSV导入）。
      4. 在原生“导出/另存为”窗口写入一个受控的新临时路径并确认（不覆盖已有文件）。
      5. 等待导出文件出现后，调用 Compare-ShineLabSequence.ps1 与期望 CSV 结构化比对；
         也可用 -SnapshotOnly 只生成导入前只读快照。
      6. 归档证据：导出 CSV 副本 + 比对 JSON + 运行日志。

    与已冻结的导入脚本 .13 完全独立：使用独立的 C# 命名空间 ShineLabRpa.Export，
    不修改也不依赖 Invoke-ShineLabCsvImport.ps1。

    判定边界：只有比对结果为 Match 时才输出可判 Verified 的证据；导出失败、
    窗口无法判断或超时一律记为 Unknown/Failed，绝不谎报成功。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedCsvPath,

    [string]$EvidenceDir,

    [string]$ShineLabPath,

    [int]$ExpectedRows = 0,

    [int]$TimeoutSeconds = 30,

    [Nullable[int]]$GridPointX,

    [Nullable[int]]$GridPointY,

    [Nullable[int]]$ExportMenuPointX,

    [Nullable[int]]$ExportMenuPointY,

    [string[]]$IgnoreFields = @('序号', '选择', '数据名称'),

    [string[]]$KeyFields,

    # 生产追加验证传 AppendDelta，并同时提供导入前 BaselineCsvPath。
    [ValidateSet('Full', 'AppendTail', 'AppendDelta')]
    [string]$MatchMode = 'Full',

    [string]$BaselineCsvPath,

    # 只导出当前序列并返回快照元数据，不做期望比对。用于实际导入前建立基线。
    [switch]$SnapshotOnly,

    [switch]$KeepConsoleVisible,

    [switch]$UseKeyboardMenuFallback,

    [switch]$UseKeyboardFileDialogFallback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ShineCsvHeader = '序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法'
$script:RpaScriptVersion = 'export-2026-08-27.2'
$script:ExportMenuNames = @('导出CSV', '导出 CSV', 'CSV导出', 'Export CSV')
$script:AnalysisTabNames = @('分析控制', 'Analysis Control')
$script:SaveButtonNames = @('保存', '导出', 'Save', 'Export')

function Write-Stage {
    param([string]$Message)
    Write-Host ("[ShineLab Export] {0}" -f $Message)
}

function Read-ExpectedCsv {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$AllowEmpty
    )

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $content = [System.IO.File]::ReadAllText($resolved, $utf8)
    $headerLine = ($content -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1).TrimEnd(',')
    if ($headerLine -ne $script:ShineCsvHeader) {
        throw "期望 CSV 表头不符合 ShineLab ExportData.csv 模板。期望：$script:ShineCsvHeader"
    }
    $rows = @($content | ConvertFrom-Csv)
    if ($rows.Count -eq 0 -and -not $AllowEmpty) {
        throw '期望 CSV 中没有任务行。'
    }
    [pscustomobject]@{ Path = $resolved; Count = $rows.Count }
}

function Add-ExportUiAutomation {
    try {
        Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
        Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    }
    catch {
        throw '无法加载 Windows UI Automation。请在 Windows PowerShell 5.1 或安装桌面运行时的环境中执行。'
    }

    if (([System.Management.Automation.PSTypeName]'ShineLabRpa.Export.NativeMethods').Type) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShineLabRpa.Export
{
    public sealed class PopupMenuSelectionResult
    {
        public bool Clicked;
        public bool PopupDetected;
        public string ItemText;
        public string Diagnostic;
    }
    public static class NativeMethods
    {
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MenuGetHandle = 0x01E1;
        private const uint MenuFlagByPosition = 0x00000400;
        private const uint SendMessageAbortIfHung = 0x0002;
        private const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X; public int Y; }

        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maximumCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maximumCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rectangle);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out UIntPtr result);
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menuHandle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menuHandle, uint item, StringBuilder text, int maximumCount, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMenuItemRect(IntPtr hWnd, IntPtr menuHandle, uint item, out Rect rectangle);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        public static void Activate(IntPtr hWnd)
        {
            ShowWindow(hWnd, 9);
            SetForegroundWindow(hWnd);
        }

        public static bool ActivateAndVerify(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) { return false; }
            ShowWindow(hWnd, 9);
            var foreground = GetForegroundWindow();
            uint ignored;
            var foregroundThreadId = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out ignored);
            var currentThreadId = GetCurrentThreadId();
            var attached = false;
            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) { AttachThreadInput(currentThreadId, foregroundThreadId, false); }
            }
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsWindowInProcess(GetForegroundWindow(), processId)) { return true; }
                Thread.Sleep(50);
            }
            return false;
        }

        public static void MinimizeConsole()
        {
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero) { ShowWindow(hWnd, 6); }
        }

        public static void RestoreConsole()
        {
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero) { ShowWindow(hWnd, 9); SetForegroundWindow(hWnd); }
        }

        public static void LeftClick(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(120);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(75);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(120);
        }

        public static string LeftClickPhysical(int x, int y)
        {
            if (!SetPhysicalCursorPos(x, y))
            {
                throw new InvalidOperationException("SetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
            }
            Thread.Sleep(150);
            Point actual;
            if (!GetPhysicalCursorPos(out actual))
            {
                throw new InvalidOperationException("GetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
            }
            if (Math.Abs(actual.X - x) > 2 || Math.Abs(actual.Y - y) > 2)
            {
                throw new InvalidOperationException(String.Format("Physical cursor mismatch: requested={0},{1}, actual={2},{3}", x, y, actual.X, actual.Y));
            }
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(90);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(150);
            return actual.X + "," + actual.Y;
        }

        public static void RightClick(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(150);
            mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(200);
        }

        public static int[] GetPhysicalCursorPosition()
        {
            Point point;
            if (!GetPhysicalCursorPos(out point))
            {
                throw new InvalidOperationException("GetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
            }
            return new int[] { point.X, point.Y };
        }

        private static void PressKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
            Thread.Sleep(75);
        }

        private static void PressModifiedKey(byte modifier, byte virtualKey)
        {
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
            PressKey(virtualKey);
            keybd_event(modifier, 0, KeyEventKeyUp, UIntPtr.Zero);
            Thread.Sleep(75);
        }

        public static IntPtr GetForegroundWindowHandle() { return GetForegroundWindow(); }
        public static IntPtr GetConsoleWindowHandle() { return GetConsoleWindow(); }
        public static IntPtr GetWindowAtPoint(int x, int y) { return WindowFromPoint(new Point { X = x, Y = y }); }

        private static string ReadWindowClass(IntPtr hWnd)
        {
            var value = new StringBuilder(256);
            GetClassName(hWnd, value, value.Capacity);
            return value.ToString();
        }

        private static string ReadWindowText(IntPtr hWnd)
        {
            var value = new StringBuilder(512);
            GetWindowText(hWnd, value, value.Capacity);
            return value.ToString();
        }

        private static string NormalizeMenuText(string value)
        {
            if (String.IsNullOrEmpty(value)) { return String.Empty; }
            var tabIndex = value.IndexOf('\t');
            if (tabIndex >= 0) { value = value.Substring(0, tabIndex); }
            return value.Replace("&", String.Empty).Replace(" ", String.Empty)
                .Replace("　", String.Empty).Trim().ToUpperInvariant();
        }

        private static bool IsExpectedMenuText(string itemText, string[] expectedNames)
        {
            var normalizedItem = NormalizeMenuText(itemText);
            foreach (var expectedName in expectedNames)
            {
                var normalizedExpected = NormalizeMenuText(expectedName);
                if (normalizedItem == normalizedExpected ||
                    normalizedItem.IndexOf(normalizedExpected, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static PopupMenuSelectionResult TryClickPopupMenuItem(int processId, string[] expectedNames, int anchorX, int anchorY)
        {
            var selection = new PopupMenuSelectionResult();
            var diagnostics = new List<string>();

            EnumWindows(delegate(IntPtr hWnd, IntPtr parameter)
            {
                if (!IsWindowVisible(hWnd)) { return true; }
                uint ownerProcessId;
                GetWindowThreadProcessId(hWnd, out ownerProcessId);
                if (ownerProcessId != (uint)processId) { return true; }

                var className = ReadWindowClass(hWnd);
                Rect windowRectangle;
                var isNearAnchor = GetWindowRect(hWnd, out windowRectangle) &&
                    anchorX >= windowRectangle.Left - 12 && anchorX <= windowRectangle.Right + 12 &&
                    anchorY >= windowRectangle.Top - 12 && anchorY <= windowRectangle.Bottom + 12;
                var windowWidth = windowRectangle.Right - windowRectangle.Left;
                var windowHeight = windowRectangle.Bottom - windowRectangle.Top;
                var isPopupSizedWindow = isNearAnchor && windowWidth > 20 && windowWidth <= 800 && windowHeight > 20 && windowHeight <= 600;
                if (className == "#32768" || className.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 || isPopupSizedWindow)
                {
                    selection.PopupDetected = true;
                }

                UIntPtr rawMenuHandle;
                var sendResult = SendMessageTimeout(hWnd, MenuGetHandle, UIntPtr.Zero, IntPtr.Zero, SendMessageAbortIfHung, 250, out rawMenuHandle);
                if (sendResult == IntPtr.Zero || rawMenuHandle == UIntPtr.Zero)
                {
                    if (isNearAnchor && diagnostics.Count < 8)
                    {
                        diagnostics.Add(String.Format("class={0}, rect={1},{2},{3},{4}, no-HMENU", className, windowRectangle.Left, windowRectangle.Top, windowRectangle.Right, windowRectangle.Bottom));
                    }
                    return true;
                }

                var menuHandle = new IntPtr(unchecked((long)rawMenuHandle.ToUInt64()));
                var itemCount = GetMenuItemCount(menuHandle);
                if (itemCount <= 0 || itemCount > 100) { return true; }

                var itemNames = new List<string>();
                for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    var itemTextBuilder = new StringBuilder(512);
                    GetMenuString(menuHandle, (uint)itemIndex, itemTextBuilder, itemTextBuilder.Capacity, MenuFlagByPosition);
                    var itemText = itemTextBuilder.ToString();
                    itemNames.Add(itemText);
                    if (!IsExpectedMenuText(itemText, expectedNames)) { continue; }

                    Rect itemRectangle;
                    if (!GetMenuItemRect(IntPtr.Zero, menuHandle, (uint)itemIndex, out itemRectangle) &&
                        !GetMenuItemRect(hWnd, menuHandle, (uint)itemIndex, out itemRectangle))
                    {
                        diagnostics.Add("matched menu text but GetMenuItemRect failed: " + itemText);
                        return true;
                    }

                    selection.Clicked = true;
                    selection.ItemText = itemText;
                    selection.Diagnostic = "HMENU verified";
                    LeftClick(itemRectangle.Left + ((itemRectangle.Right - itemRectangle.Left) / 2), itemRectangle.Top + ((itemRectangle.Bottom - itemRectangle.Top) / 2));
                    return false;
                }

                diagnostics.Add(String.Format("class={0}, HMENU items=[{1}]", className, String.Join(" | ", itemNames.ToArray())));
                return true;
            }, IntPtr.Zero);

            if (!selection.Clicked)
            {
                selection.Diagnostic = diagnostics.Count == 0
                    ? "no visible ShineLab popup menu window was detected"
                    : String.Join("; ", diagnostics.ToArray());
            }
            return selection;
        }

        // 导出使用“保存/导出/另存为”窗口，与导入的“打开”窗口相反。
        public static bool IsSaveFileDialogWindow(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) { return false; }
            uint ownerProcessId;
            GetWindowThreadProcessId(hWnd, out ownerProcessId);
            if (ownerProcessId != (uint)processId) { return false; }

            var title = ReadWindowText(hWnd);
            var titleUpper = title.ToUpperInvariant();
            // 明确排除“打开”窗口，避免与导入路径混淆。
            if (title.Contains("打开") || titleUpper.Contains("OPEN")) { return false; }
            return title.Contains("保存") || title.Contains("导出") || title.Contains("另存为") ||
                titleUpper.Contains("SAVE") || titleUpper.Contains("EXPORT");
        }

        public static string DescribeWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return "handle=0"; }
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            return String.Format("handle=0x{0:X}, pid={1}, class={2}, title={3}", hWnd.ToInt64(), processId, ReadWindowClass(hWnd), ReadWindowText(hWnd));
        }

        public static bool IsWindowHandle(IntPtr hWnd) { return hWnd != IntPtr.Zero && IsWindow(hWnd); }

        public static bool IsWindowInProcess(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) { return false; }
            uint ownerProcessId;
            GetWindowThreadProcessId(hWnd, out ownerProcessId);
            return ownerProcessId == (uint)processId;
        }

        public static void FocusNativeFileName() { PressModifiedKey(0x12, 0x4E); } // Alt+N

        public static void PasteAndConfirmNativeFile()
        {
            PressModifiedKey(0x11, 0x41); // Ctrl+A
            PressModifiedKey(0x11, 0x56); // Ctrl+V
            PressKey(0x0D);               // Enter：确认保存路径
        }

        public static void DismissContextMenu() { PressKey(0x1B); } // Escape

        public static void SelectFirstContextMenuItem()
        {
            // 已验证的两项 XTP 菜单首项为“导出CSV”。
            PressKey(0x24); // Home：定位到首项，无论初始高亮在哪
            PressKey(0x0D); // Enter
        }
    }
}
'@
}
function New-NameCondition {
    param([string]$Name)
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
}

function Find-ElementByNames {
    param([Parameter(Mandatory = $true)]$Root, [Parameter(Mandatory = $true)][string[]]$Names)
    foreach ($name in $Names) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-NameCondition $name))
        if ($null -ne $element) { return $element }
    }
    return $null
}

function Wait-ElementByNames {
    param([Parameter(Mandatory = $true)]$Root, [Parameter(Mandatory = $true)][string[]]$Names, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = Find-ElementByNames -Root $Root -Names $Names
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-UiElement {
    param([Parameter(Mandatory = $true)]$Element)
    try {
        $invoke = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        return
    }
    catch {
        $point = $Element.Current.BoundingRectangle
        if ($point.Width -le 0 -or $point.Height -le 0) { throw '界面元素不可点击。' }
        [ShineLabRpa.Export.NativeMethods]::LeftClick([int]($point.Left + ($point.Width / 2)), [int]($point.Top + ($point.Height / 2)))
    }
}

function Get-ElementPoint {
    param([Parameter(Mandatory = $true)]$Element)
    $rectangle = $Element.Current.BoundingRectangle
    if ($rectangle.Width -le 0 -or $rectangle.Height -le 0) {
        throw '任务表没有可用的屏幕坐标。请使用 -GridPointX 和 -GridPointY 指定表格位置。'
    }
    [pscustomobject]@{ X = [int]($rectangle.Left + ($rectangle.Width / 2)); Y = [int]($rectangle.Top + ($rectangle.Height / 2)) }
}

function Get-MainWindowProcess {
    param([string]$ExecutablePath)
    $match = { $_.MainWindowHandle -ne 0 -and ($_.MainWindowTitle -match 'ShineDataAcquisition|ShineDataAcquire|ShineLab') }
    $process = Get-Process | Where-Object $match | Select-Object -First 1
    if ($null -eq $process -and -not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $fullPath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
        Write-Stage "启动 ShineLab：$fullPath"
        Start-Process -FilePath $fullPath -WorkingDirectory (Split-Path -Parent $fullPath) | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process | Where-Object $match | Select-Object -First 1
        if ($null -ne $process) { return $process }
        Start-Sleep -Milliseconds 250
    }
    throw '未找到 ShineDataAcquisition 主窗口。请先打开 ShineLab，或通过 -ShineLabPath 提供可执行文件路径。'
}
function Find-TaskGrid {
    param([Parameter(Mandatory = $true)]$Window)
    $types = @(
        [System.Windows.Automation.ControlType]::DataGrid,
        [System.Windows.Automation.ControlType]::Table,
        [System.Windows.Automation.ControlType]::List)
    foreach ($type in $types) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
        $elements = @($Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
        foreach ($element in $elements) {
            $rectangle = $element.Current.BoundingRectangle
            if (-not $element.Current.IsOffscreen -and $rectangle.Width -ge 400 -and $rectangle.Height -ge 120) {
                return $element
            }
        }
    }
    return $null
}

function Save-DesktopScreenshot {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        if ([System.IO.File]::Exists($Path)) { [System.IO.File]::Delete($Path) }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Compare-ScreenshotRegion {
    param(
        [Parameter(Mandatory = $true)][string]$BeforePath,
        [Parameter(Mandatory = $true)][string]$AfterPath,
        [Parameter(Mandatory = $true)][int]$CenterX,
        [Parameter(Mandatory = $true)][int]$CenterY,
        [int]$HalfWidth = 300, [int]$HalfHeight = 180, [int]$SampleStep = 2, [int]$ColorThreshold = 30)

    $before = [System.Drawing.Bitmap]::new($BeforePath)
    $after = [System.Drawing.Bitmap]::new($AfterPath)
    try {
        if ($before.Width -ne $after.Width -or $before.Height -ne $after.Height) {
            throw '右键前后截图尺寸不一致，无法校验菜单。'
        }
        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $left = [Math]::Max($screen.Left, $CenterX - $HalfWidth)
        $top = [Math]::Max($screen.Top, $CenterY - $HalfHeight)
        $right = [Math]::Min($screen.Right - 1, $CenterX + $HalfWidth)
        $bottom = [Math]::Min($screen.Bottom - 1, $CenterY + $HalfHeight)
        $changed = 0
        for ($screenY = $top; $screenY -le $bottom; $screenY += $SampleStep) {
            $bitmapY = $screenY - $screen.Top
            for ($screenX = $left; $screenX -le $right; $screenX += $SampleStep) {
                $bitmapX = $screenX - $screen.Left
                $b = $before.GetPixel($bitmapX, $bitmapY)
                $a = $after.GetPixel($bitmapX, $bitmapY)
                $difference = [Math]::Max([Math]::Abs([int]$b.R - [int]$a.R),
                    [Math]::Max([Math]::Abs([int]$b.G - [int]$a.G), [Math]::Abs([int]$b.B - [int]$a.B)))
                if ($difference -ge $ColorThreshold) { $changed++ }
            }
        }
        return $changed
    }
    finally { $before.Dispose(); $after.Dispose() }
}
function Set-FileDialogPath {
    param([Parameter(Mandatory = $true)]$Dialog, [Parameter(Mandatory = $true)][string]$Path)
    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = @($Dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition))
    $candidates = @(
        $edits | Where-Object { $_.Current.AutomationId -eq '1148' }
        $edits | Where-Object { $_.Current.Name -match '文件名|File name' }
        $edits)
    foreach ($edit in $candidates) {
        try {
            $value = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $value.SetValue($Path)
            return
        }
        catch { continue }
    }
    throw '未找到文件选择框中的文件名输入框。'
}

function Find-SaveDialog {
    param([Parameter(Mandatory = $true)][int]$ProcessId)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    foreach ($window in @($desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $windowCondition))) {
        try {
            $name = $window.Current.Name
            if ($window.Current.ProcessId -eq $ProcessId -and
                $name -match '保存|导出|另存为|Save|Export' -and
                $name -notmatch '打开|Open') {
                return $window
            }
        }
        catch { continue }
    }
    return $null
}

function Wait-SaveDialog {
    param([Parameter(Mandatory = $true)][int]$ProcessId, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $dialog = Find-SaveDialog -ProcessId $ProcessId
        if ($null -ne $dialog) { return $dialog }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Wait-SaveDialogForeground {
    param([Parameter(Mandatory = $true)][IntPtr]$MainWindowHandle, [Parameter(Mandatory = $true)][int]$ProcessId, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $consoleHandle = [ShineLabRpa.Export.NativeMethods]::GetConsoleWindowHandle()
    while ([DateTime]::UtcNow -lt $deadline) {
        $foreground = [ShineLabRpa.Export.NativeMethods]::GetForegroundWindowHandle()
        if ($foreground -ne [IntPtr]::Zero -and $foreground -ne $MainWindowHandle -and $foreground -ne $consoleHandle -and
            [ShineLabRpa.Export.NativeMethods]::IsSaveFileDialogWindow($foreground, $ProcessId)) {
            return $foreground
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Wait-WindowClosed {
    param([Parameter(Mandatory = $true)][IntPtr]$Handle, [int]$Seconds = 5)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not [ShineLabRpa.Export.NativeMethods]::IsWindowHandle($Handle)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return -not [ShineLabRpa.Export.NativeMethods]::IsWindowHandle($Handle)
}

function Wait-FileStable {
    param([Parameter(Mandatory = $true)][string]$Path, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $lastLength = -1L
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([System.IO.File]::Exists($Path)) {
            $length = (Get-Item -LiteralPath $Path).Length
            if ($length -gt 0 -and $length -eq $lastLength) { return $true }
            $lastLength = $length
        }
        Start-Sleep -Milliseconds 400
    }
    return ([System.IO.File]::Exists($Path) -and (Get-Item -LiteralPath $Path).Length -gt 0)
}
$expected = Read-ExpectedCsv -Path $ExpectedCsvPath
Write-Stage "脚本版本：$script:RpaScriptVersion"
Write-Stage "期望 CSV 校验通过：$($expected.Count) 条任务，文件 $($expected.Path)"
if ($ExpectedRows -gt 0 -and $expected.Count -ne $ExpectedRows) {
    throw "期望 CSV 任务数为 $($expected.Count)，与 -ExpectedRows $ExpectedRows 不一致。"
}

if (($null -eq $ExportMenuPointX) -xor ($null -eq $ExportMenuPointY)) {
    throw '导出菜单点击坐标必须同时提供 -ExportMenuPointX 和 -ExportMenuPointY。'
}
$useExportMenuPoint = $null -ne $ExportMenuPointX -and $null -ne $ExportMenuPointY

if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path (Split-Path -Parent $expected.Path) 'shinelab-export-evidence'
}
$null = New-Item -ItemType Directory -Path $EvidenceDir -Force
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
# 受控的新导出目标：必须不存在，避免触发覆盖确认，也不动现有任何文件。
$exportTargetPath = Join-Path $EvidenceDir ("shinelab-export-{0}.csv" -f $stamp)
if ([System.IO.File]::Exists($exportTargetPath)) {
    throw "导出目标已存在，拒绝覆盖：$exportTargetPath"
}
$comparePath = Join-Path $EvidenceDir ("shinelab-export-compare-{0}.json" -f $stamp)
Write-Stage "导出目标（受控新文件）：$exportTargetPath"

Add-ExportUiAutomation
$process = Get-MainWindowProcess -ExecutablePath $ShineLabPath
$process.Refresh()
[ShineLabRpa.Export.NativeMethods]::Activate($process.MainWindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

$analysisTab = Find-ElementByNames -Root $window -Names $script:AnalysisTabNames
if ($null -ne $analysisTab) { Invoke-UiElement -Element $analysisTab; Start-Sleep -Milliseconds 500 }

$grid = Find-TaskGrid -Window $window
if ($null -ne $GridPointX -and $null -ne $GridPointY) {
    $gridPoint = [pscustomobject]@{ X = [int]$GridPointX; Y = [int]$GridPointY }
}
elseif ($null -ne $grid) {
    $gridPoint = Get-ElementPoint -Element $grid
}
else {
    throw '未定位到样品任务表。请先在分析控制中打开序列，或提供 -GridPointX/-GridPointY。'
}

$consoleMinimized = $false
$menuBeforePath = Join-Path $EvidenceDir 'ShineLab-export-menu-before.tmp.png'
$menuEvidencePath = Join-Path $EvidenceDir ("shinelab-export-menu-{0}.png" -f $stamp)
try {
    if (-not $KeepConsoleVisible) {
        [ShineLabRpa.Export.NativeMethods]::MinimizeConsole()
        $consoleMinimized = $true
        Start-Sleep -Milliseconds 500
    }
    $activated = [ShineLabRpa.Export.NativeMethods]::ActivateAndVerify($process.MainWindowHandle, $process.Id)
    Start-Sleep -Milliseconds 300
    $foregroundHandle = [ShineLabRpa.Export.NativeMethods]::GetForegroundWindowHandle()
    if (-not $activated -or -not [ShineLabRpa.Export.NativeMethods]::IsWindowInProcess($foregroundHandle, $process.Id)) {
        throw ('无法将 ShineLab 切换到前台，未执行坐标右键。当前前台窗口：{0}' -f [ShineLabRpa.Export.NativeMethods]::DescribeWindow($foregroundHandle))
    }
    Write-Stage "已确认 ShineLab 位于前台：$([ShineLabRpa.Export.NativeMethods]::DescribeWindow($foregroundHandle))"

    $pointWindowHandle = [ShineLabRpa.Export.NativeMethods]::GetWindowAtPoint($gridPoint.X, $gridPoint.Y)
    if (-not [ShineLabRpa.Export.NativeMethods]::IsWindowInProcess($pointWindowHandle, $process.Id)) {
        throw ('任务表坐标没有命中 ShineLab 窗口，未执行右键。坐标：{0},{1}；命中窗口：{2}' -f $gridPoint.X, $gridPoint.Y, [ShineLabRpa.Export.NativeMethods]::DescribeWindow($pointWindowHandle))
    }
    Write-Stage "任务表坐标命中窗口：$([ShineLabRpa.Export.NativeMethods]::DescribeWindow($pointWindowHandle))"

    Save-DesktopScreenshot -Path $menuBeforePath
    Write-Stage "向任务表发送右键输入：$($gridPoint.X),$($gridPoint.Y)"
    [ShineLabRpa.Export.NativeMethods]::RightClick($gridPoint.X, $gridPoint.Y)
    $physicalCursor = [ShineLabRpa.Export.NativeMethods]::GetPhysicalCursorPosition()
    Start-Sleep -Milliseconds 600
    Save-DesktopScreenshot -Path $menuEvidencePath
    $changed = Compare-ScreenshotRegion -BeforePath $menuBeforePath -AfterPath $menuEvidencePath -CenterX ([int]$physicalCursor[0]) -CenterY ([int]$physicalCursor[1])
    Write-Stage ('右键前后界面变化：采样点 {0}' -f $changed)
    if ($changed -lt 80) {
        [ShineLabRpa.Export.NativeMethods]::DismissContextMenu()
        throw ('未检测到右键菜单所需的界面变化，已停止且未点击菜单。证据截图：{0}' -f $menuEvidencePath)
    }
    Write-Stage "右键结果校验通过，证据截图：$menuEvidencePath"

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $menuWaitSeconds = if ($UseKeyboardMenuFallback -or $useExportMenuPoint) { 2 } else { $TimeoutSeconds }
    $exportMenu = Wait-ElementByNames -Root $desktop -Names $script:ExportMenuNames -Seconds $menuWaitSeconds
    if ($null -ne $exportMenu) {
        Write-Stage '已通过 UI Automation 定位“导出CSV”。'
        Invoke-UiElement -Element $exportMenu
    }
    else {
        $nativeMenu = [ShineLabRpa.Export.NativeMethods]::TryClickPopupMenuItem($process.Id, [string[]]$script:ExportMenuNames, $gridPoint.X, $gridPoint.Y)
        if ($nativeMenu.Clicked) {
            Write-Stage "已校验 Win32 菜单文本并点击：$($nativeMenu.ItemText)"
        }
        elseif ($useExportMenuPoint) {
            $menuPointWindowHandle = [ShineLabRpa.Export.NativeMethods]::GetWindowAtPoint([int]$ExportMenuPointX, [int]$ExportMenuPointY)
            if (-not [ShineLabRpa.Export.NativeMethods]::IsWindowInProcess($menuPointWindowHandle, $process.Id)) {
                throw ('导出菜单坐标没有命中 ShineLab 窗口，未执行左键。坐标：{0},{1}' -f $ExportMenuPointX, $ExportMenuPointY)
            }
            Write-Stage ('按坐标点击首项“导出CSV”：{0},{1}' -f $ExportMenuPointX, $ExportMenuPointY)
            [ShineLabRpa.Export.NativeMethods]::LeftClickPhysical([int]$ExportMenuPointX, [int]$ExportMenuPointY) | Out-Null
        }
        elseif ($UseKeyboardMenuFallback -and [ShineLabRpa.Export.NativeMethods]::IsWindowInProcess([ShineLabRpa.Export.NativeMethods]::GetForegroundWindowHandle(), $process.Id)) {
            Write-Stage '使用已确认的两项菜单顺序：按 Home 键选择首项“导出CSV”。'
            [ShineLabRpa.Export.NativeMethods]::SelectFirstContextMenuItem()
        }
        else {
            throw ('未找到“导出CSV”菜单。Win32诊断：{0}' -f $nativeMenu.Diagnostic)
        }
    }

    $dialogWaitSeconds = if ($UseKeyboardFileDialogFallback) { 2 } else { $TimeoutSeconds }
    $dialog = Wait-SaveDialog -ProcessId $process.Id -Seconds $dialogWaitSeconds
    if ($null -ne $dialog) {
        $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
        Write-Stage "已检测到 ShineLab 导出文件窗口：$($dialog.Current.Name)"
        Set-FileDialogPath -Dialog $dialog -Path $exportTargetPath
        $saveButton = Find-ElementByNames -Root $dialog -Names $script:SaveButtonNames
        if ($null -eq $saveButton) { throw '未找到文件选择框的“保存”按钮。' }
        Invoke-UiElement -Element $saveButton
        if ($dialogHandle -ne [IntPtr]::Zero -and -not (Wait-WindowClosed -Handle $dialogHandle)) {
            throw '导出文件窗口仍未关闭，导出路径可能未写入正确的输入框。'
        }
    }
    elseif ($UseKeyboardFileDialogFallback) {
        Write-Stage '文件窗口未暴露给 UI Automation，检查 ShineLab 前台原生“保存/导出”窗口。'
        $dialogHandle = Wait-SaveDialogForeground -MainWindowHandle $process.MainWindowHandle -ProcessId $process.Id -Seconds $TimeoutSeconds
        if ($dialogHandle -eq [IntPtr]::Zero) {
            throw ('未检测到 ShineLab 的“保存/导出文件”窗口；很可能没有触发“导出CSV”。当前前台窗口：{0}' -f [ShineLabRpa.Export.NativeMethods]::DescribeWindow([ShineLabRpa.Export.NativeMethods]::GetForegroundWindowHandle()))
        }
        Write-Stage "已检测到原生导出文件窗口：$([ShineLabRpa.Export.NativeMethods]::DescribeWindow($dialogHandle))"
        try { Set-Clipboard -Value $exportTargetPath -ErrorAction Stop }
        catch { throw '无法写入剪贴板，无法执行文件选择框键盘兜底。' }
        [ShineLabRpa.Export.NativeMethods]::Activate($dialogHandle)
        [ShineLabRpa.Export.NativeMethods]::FocusNativeFileName()
        [ShineLabRpa.Export.NativeMethods]::PasteAndConfirmNativeFile()
        if (-not (Wait-WindowClosed -Handle $dialogHandle)) {
            throw '导出文件窗口仍未关闭，导出路径未被确认；未报告导出成功。'
        }
    }
    else {
        throw '未找到导出文件选择框。'
    }

    Write-Stage '已提交导出，等待导出文件写盘。'
    if (-not (Wait-FileStable -Path $exportTargetPath -Seconds $TimeoutSeconds)) {
        throw "导出文件未在超时内生成或为空：$exportTargetPath（状态：Unknown，请人工确认）。"
    }
    Write-Stage "导出文件已生成：$exportTargetPath"
}
finally {
    if ([System.IO.File]::Exists($menuBeforePath)) { [System.IO.File]::Delete($menuBeforePath) }
    if ($consoleMinimized) { [ShineLabRpa.Export.NativeMethods]::RestoreConsole() }
}

if ($SnapshotOnly) {
    $snapshot = Read-ExpectedCsv -Path $exportTargetPath -AllowEmpty
    $snapshotResult = [pscustomobject]@{
        schemaVersion = '1.0'
        generatedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        status = 'Snapshot'
        exportedCsvPath = $snapshot.Path
        sha256 = (Get-FileHash -LiteralPath $snapshot.Path -Algorithm SHA256).Hash
        rowCount = $snapshot.Count
    }
    Write-Stage "只读快照完成：$($snapshot.Count) 行。未导入、未点击“运行”。"
    $snapshotResult | ConvertTo-Json -Depth 4
    return
}

# 结构化比对（只读）：调用确定性比对内核。
$compareScript = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) 'Compare-ShineLabSequence.ps1'
$compareArgs = @{
    ActualCsvPath   = $exportTargetPath
    ExpectedCsvPath = $expected.Path
    OutputJsonPath  = $comparePath
    IgnoreFields    = $IgnoreFields
    MatchMode       = $MatchMode
    AsObject        = $true
}
if ($KeyFields) { $compareArgs['KeyFields'] = $KeyFields }
if ($MatchMode -eq 'AppendDelta') {
    if ([string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
        throw 'AppendDelta 必须指定 -BaselineCsvPath。'
    }
    $compareArgs['BaselineCsvPath'] = $BaselineCsvPath
}
$result = & $compareScript @compareArgs

Write-Stage ("比对结果：{0}（差异 {1} 处），JSON：{2}" -f $result.status, $result.differenceCount, $comparePath)
if ($result.status -eq 'Match') {
    Write-Stage '只读导出验证：当前序列与期望 CSV 一致（业务关键字段）。脚本未导入、未点击“运行”。'
}
else {
    Write-Warning '只读导出验证：当前序列与期望 CSV 不一致。请查看比对 JSON 的 differences。'
}
$result | ConvertTo-Json -Depth 6









