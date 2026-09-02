[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CsvPath,

    [string]$ShineLabPath,

    [int]$ExpectedRows = 0,

    [int]$TimeoutSeconds = 30,

    [Nullable[int]]$GridPointX,

    [Nullable[int]]$GridPointY,

    [Nullable[int]]$ImportMenuPointX,

    [Nullable[int]]$ImportMenuPointY,

    [switch]$ExecuteImport,

    [switch]$AllowAppend,

    [switch]$KeepConsoleVisible,

    [switch]$ProbeContextMenu,

    [switch]$UseKeyboardMenuFallback,

    [switch]$UseKeyboardFileDialogFallback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ShineCsvHeader = '序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法'
$script:RpaScriptVersion = '2026-08-24.13'
$script:ImportMenuNames = @('从CSV导入', '从 CSV 导入', 'CSV导入', 'Import CSV')
$script:AnalysisTabNames = @('分析控制', 'Analysis Control')
$script:OpenButtonNames = @('打开', 'Open')

function Write-Stage {
    param([string]$Message)
    Write-Host ("[ShineLab RPA] {0}" -f $Message)
}

function Read-ShineCsv {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    $resolved = $resolvedPath.ProviderPath
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $content = [System.IO.File]::ReadAllText($resolved, $utf8)
    $headerLine = ($content -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1).TrimEnd(',')
    if ($headerLine -ne $script:ShineCsvHeader) {
        throw "CSV表头不符合 ShineLab ExportData.csv 模板。期望：$script:ShineCsvHeader"
    }

    try {
        $rows = @($content | ConvertFrom-Csv)
    }
    catch {
        throw "CSV解析失败：$($_.Exception.Message)"
    }

    if ($rows.Count -eq 0) {
        throw 'CSV中没有可导入的任务行。'
    }

    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace([string]$row.'样品类型')) {
            throw "CSV包含空的样品类型，序号 $($row.'序号')。"
        }
        $cycleCount = 0
        if ([string]::IsNullOrWhiteSpace([string]$row.'循环次数') -or
            -not [int]::TryParse([string]$row.'循环次数', [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$cycleCount)) {
            throw "CSV包含无效的循环次数，序号 $($row.'序号')。"
        }
    }

    [pscustomobject]@{
        Path = $resolved
        Rows = $rows
        Count = $rows.Count
    }
}

function Add-UiAutomationAssemblies {
    try {
        Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
        Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    }
    catch {
        throw '无法加载 Windows UI Automation。请在 Windows PowerShell 5.1 或安装桌面运行时的 PowerShell 环境中执行。'
    }

    if (-not ([System.Management.Automation.PSTypeName]'ShineLabRpa.NativeMethods').Type) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShineLabRpa
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(
            uint threadIdAttach,
            uint threadIdAttachTo,
            bool attach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maximumCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rectangle);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr menuHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMenuString(
            IntPtr menuHandle,
            uint item,
            StringBuilder text,
            int maximumCount,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMenuItemRect(
            IntPtr hWnd,
            IntPtr menuHandle,
            uint item,
            out Rect rectangle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetPhysicalCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPhysicalCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private const uint KeyEventKeyUp = 0x0002;

        public static void Activate(IntPtr hWnd)
        {
            ShowWindow(hWnd, 9);
            SetForegroundWindow(hWnd);
        }

        public static bool ActivateAndVerify(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                return false;
            }

            ShowWindow(hWnd, 9);
            var foreground = GetForegroundWindow();
            uint ignoredProcessId;
            var foregroundThreadId = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out ignoredProcessId);
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
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }

            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsWindowInProcess(GetForegroundWindow(), processId))
                {
                    return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        public static void MinimizeConsole()
        {
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, 6);
            }
        }

        public static void RestoreConsole()
        {
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, 9);
                SetForegroundWindow(hWnd);
            }
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
                throw new InvalidOperationException(
                    "SetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
            }
            Thread.Sleep(150);

            Point actual;
            if (!GetPhysicalCursorPos(out actual))
            {
                throw new InvalidOperationException(
                    "GetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
            }
            if (Math.Abs(actual.X - x) > 2 || Math.Abs(actual.Y - y) > 2)
            {
                throw new InvalidOperationException(String.Format(
                    "Physical cursor mismatch: requested={0},{1}, actual={2},{3}",
                    x,
                    y,
                    actual.X,
                    actual.Y));
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
                throw new InvalidOperationException(
                    "GetPhysicalCursorPos failed with Win32 error " + Marshal.GetLastWin32Error());
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

        public static IntPtr GetForegroundWindowHandle()
        {
            return GetForegroundWindow();
        }

        public static IntPtr GetConsoleWindowHandle()
        {
            return GetConsoleWindow();
        }

        public static IntPtr GetWindowAtPoint(int x, int y)
        {
            return WindowFromPoint(new Point { X = x, Y = y });
        }

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
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            var tabIndex = value.IndexOf('\t');
            if (tabIndex >= 0)
            {
                value = value.Substring(0, tabIndex);
            }
            return value.Replace("&", String.Empty)
                .Replace(" ", String.Empty)
                .Replace("\u3000", String.Empty)
                .Trim()
                .ToUpperInvariant();
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

        public static PopupMenuSelectionResult TryClickPopupMenuItem(
            int processId,
            string[] expectedNames,
            int anchorX,
            int anchorY)
        {
            var selection = new PopupMenuSelectionResult();
            var diagnostics = new List<string>();

            EnumWindows(delegate(IntPtr hWnd, IntPtr parameter)
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                uint ownerProcessId;
                GetWindowThreadProcessId(hWnd, out ownerProcessId);
                if (ownerProcessId != (uint)processId)
                {
                    return true;
                }

                var className = ReadWindowClass(hWnd);
                Rect windowRectangle;
                var isNearAnchor = GetWindowRect(hWnd, out windowRectangle) &&
                    anchorX >= windowRectangle.Left - 12 && anchorX <= windowRectangle.Right + 12 &&
                    anchorY >= windowRectangle.Top - 12 && anchorY <= windowRectangle.Bottom + 12;
                var windowWidth = windowRectangle.Right - windowRectangle.Left;
                var windowHeight = windowRectangle.Bottom - windowRectangle.Top;
                var isPopupSizedWindow = isNearAnchor &&
                    windowWidth > 20 && windowWidth <= 800 &&
                    windowHeight > 20 && windowHeight <= 600;
                if (className == "#32768" ||
                    className.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    isPopupSizedWindow)
                {
                    selection.PopupDetected = true;
                }

                UIntPtr rawMenuHandle;
                var sendResult = SendMessageTimeout(
                    hWnd,
                    MenuGetHandle,
                    UIntPtr.Zero,
                    IntPtr.Zero,
                    SendMessageAbortIfHung,
                    250,
                    out rawMenuHandle);
                if (sendResult == IntPtr.Zero || rawMenuHandle == UIntPtr.Zero)
                {
                    if (isNearAnchor && diagnostics.Count < 8)
                    {
                        diagnostics.Add(String.Format(
                            "class={0}, rect={1},{2},{3},{4}, no-HMENU",
                            className,
                            windowRectangle.Left,
                            windowRectangle.Top,
                            windowRectangle.Right,
                            windowRectangle.Bottom));
                    }
                    return true;
                }

                var menuHandle = new IntPtr(unchecked((long)rawMenuHandle.ToUInt64()));
                var itemCount = GetMenuItemCount(menuHandle);
                if (itemCount <= 0 || itemCount > 100)
                {
                    return true;
                }

                var itemNames = new List<string>();
                for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    var itemTextBuilder = new StringBuilder(512);
                    GetMenuString(
                        menuHandle,
                        (uint)itemIndex,
                        itemTextBuilder,
                        itemTextBuilder.Capacity,
                        MenuFlagByPosition);
                    var itemText = itemTextBuilder.ToString();
                    itemNames.Add(itemText);
                    if (!IsExpectedMenuText(itemText, expectedNames))
                    {
                        continue;
                    }

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
                    LeftClick(
                        itemRectangle.Left + ((itemRectangle.Right - itemRectangle.Left) / 2),
                        itemRectangle.Top + ((itemRectangle.Bottom - itemRectangle.Top) / 2));
                    return false;
                }

                diagnostics.Add(String.Format(
                    "class={0}, HMENU items=[{1}]",
                    className,
                    String.Join(" | ", itemNames.ToArray())));
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

        public static bool IsOpenFileDialogWindow(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                return false;
            }

            uint ownerProcessId;
            GetWindowThreadProcessId(hWnd, out ownerProcessId);
            if (ownerProcessId != (uint)processId)
            {
                return false;
            }

            var title = ReadWindowText(hWnd);
            var titleUpper = title.ToUpperInvariant();
            if (title.Contains("保存") || title.Contains("导出") ||
                titleUpper.Contains("SAVE") || titleUpper.Contains("EXPORT"))
            {
                return false;
            }

            return title.Contains("打开") || title.Contains("选择") ||
                titleUpper.Contains("OPEN") || titleUpper.Contains("SELECT");
        }

        public static string DescribeWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return "handle=0";
            }
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            return String.Format(
                "handle=0x{0:X}, pid={1}, class={2}, title={3}",
                hWnd.ToInt64(),
                processId,
                ReadWindowClass(hWnd),
                ReadWindowText(hWnd));
        }

        public static bool IsWindowHandle(IntPtr hWnd)
        {
            return hWnd != IntPtr.Zero && IsWindow(hWnd);
        }

        public static bool IsWindowInProcess(IntPtr hWnd, int processId)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                return false;
            }
            uint ownerProcessId;
            GetWindowThreadProcessId(hWnd, out ownerProcessId);
            return ownerProcessId == (uint)processId;
        }

        public static void FocusNativeFileName()
        {
            PressModifiedKey(0x12, 0x4E); // Alt+N (File name)
        }

        public static void PasteAndOpenNativeFile()
        {
            PressModifiedKey(0x11, 0x41); // Ctrl+A
            PressModifiedKey(0x11, 0x56); // Ctrl+V
            PressKey(0x0D); // Open the selected CSV exactly once.
        }

        public static void DismissContextMenu()
        {
            PressKey(0x1B); // Escape
        }

        public static void SelectLastContextMenuItem()
        {
            // The verified two-item XTP menu ends with Import CSV.
            PressKey(0x23); // End: last item regardless of initial highlight.
            PressKey(0x0D); // Enter
        }
    }
}
'@
    }
}

function Get-MainWindowProcess {
    param([string]$ExecutablePath)

    $process = Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and
        ($_.MainWindowTitle -match 'ShineDataAcquisition|ShineDataAcquire|ShineLab')
    } | Select-Object -First 1

    if ($null -eq $process -and -not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $fullPath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
        Write-Stage "启动 ShineLab：$fullPath"
        Start-Process -FilePath $fullPath -WorkingDirectory (Split-Path -Parent $fullPath) | Out-Null
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process | Where-Object {
            $_.MainWindowHandle -ne 0 -and
            ($_.MainWindowTitle -match 'ShineDataAcquisition|ShineDataAcquire|ShineLab')
        } | Select-Object -First 1
        if ($null -ne $process) {
            return $process
        }
        Start-Sleep -Milliseconds 250
    }

    throw '未找到 ShineDataAcquisition 主窗口。请先打开 ShineLab，或通过 -ShineLabPath 提供可执行文件路径。'
}

function Save-DesktopScreenshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $bounds.Location,
            [System.Drawing.Point]::Empty,
            $bounds.Size)
        if ([System.IO.File]::Exists($Path)) {
            [System.IO.File]::Delete($Path)
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Compare-ScreenshotRegion {
    param(
        [Parameter(Mandatory = $true)][string]$BeforePath,
        [Parameter(Mandatory = $true)][string]$AfterPath,
        [Parameter(Mandatory = $true)][int]$CenterX,
        [Parameter(Mandatory = $true)][int]$CenterY,
        [int]$HalfWidth = 300,
        [int]$HalfHeight = 180,
        [int]$SampleStep = 2,
        [int]$ColorThreshold = 30
    )

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
        $minX = [int]::MaxValue
        $minY = [int]::MaxValue
        $maxX = [int]::MinValue
        $maxY = [int]::MinValue

        for ($screenY = $top; $screenY -le $bottom; $screenY += $SampleStep) {
            $bitmapY = $screenY - $screen.Top
            for ($screenX = $left; $screenX -le $right; $screenX += $SampleStep) {
                $bitmapX = $screenX - $screen.Left
                $beforeColor = $before.GetPixel($bitmapX, $bitmapY)
                $afterColor = $after.GetPixel($bitmapX, $bitmapY)
                $difference = [Math]::Max(
                    [Math]::Abs([int]$beforeColor.R - [int]$afterColor.R),
                    [Math]::Max(
                        [Math]::Abs([int]$beforeColor.G - [int]$afterColor.G),
                        [Math]::Abs([int]$beforeColor.B - [int]$afterColor.B)))
                if ($difference -lt $ColorThreshold) {
                    continue
                }

                $changed++
                if ($screenX -lt $minX) { $minX = $screenX }
                if ($screenX -gt $maxX) { $maxX = $screenX }
                if ($screenY -lt $minY) { $minY = $screenY }
                if ($screenY -gt $maxY) { $maxY = $screenY }
            }
        }

        $bounds = if ($changed -gt 0) {
            '{0},{1}-{2},{3}' -f $minX, $minY, $maxX, $maxY
        }
        else {
            'none'
        }
        [pscustomobject]@{
            ChangedSamples = $changed
            Bounds = $bounds
        }
    }
    finally {
        $before.Dispose()
        $after.Dispose()
    }
}

function New-NameCondition {
    param([string]$Name)
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
}

function Find-ElementByNames {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    foreach ($name in $Names) {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-NameCondition $name))
        if ($null -ne $element) {
            return $element
        }
    }
    return $null
}

function Wait-ElementByNames {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [int]$Seconds = $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = Find-ElementByNames -Root $Root -Names $Names
        if ($null -ne $element) {
            return $element
        }
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
        if ($point.Width -le 0 -or $point.Height -le 0) {
            throw '界面元素不可点击。'
        }
        [ShineLabRpa.NativeMethods]::LeftClick(
            [int]($point.Left + ($point.Width / 2)),
            [int]($point.Top + ($point.Height / 2)))
    }
}

function Get-ElementPoint {
    param([Parameter(Mandatory = $true)]$Element)
    $rectangle = $Element.Current.BoundingRectangle
    if ($rectangle.Width -le 0 -or $rectangle.Height -le 0) {
        throw '任务表没有可用的屏幕坐标。请使用 -GridPointX 和 -GridPointY 指定表格位置。'
    }
    [pscustomobject]@{
        X = [int]($rectangle.Left + ($rectangle.Width / 2))
        Y = [int]($rectangle.Top + ($rectangle.Height / 2))
    }
}

function Find-TaskGrid {
    param([Parameter(Mandatory = $true)]$Window)

    $types = @(
        [System.Windows.Automation.ControlType]::DataGrid,
        [System.Windows.Automation.ControlType]::Table,
        [System.Windows.Automation.ControlType]::List
    )
    foreach ($type in $types) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $type)
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

function Get-TaskRowCount {
    param([Parameter(Mandatory = $true)]$Grid)

    foreach ($type in @(
        [System.Windows.Automation.ControlType]::DataItem,
        [System.Windows.Automation.ControlType]::ListItem,
        [System.Windows.Automation.ControlType]::Custom
    )) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $type)
        $children = @($Grid.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
        if ($children.Count -gt 0) {
            return $children.Count
        }
    }
    return $null
}

function Set-FileDialogPath {
    param(
        [Parameter(Mandatory = $true)]$Dialog,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = @($Dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition))
    $candidates = @(
        $edits | Where-Object { $_.Current.AutomationId -eq '1148' }
        $edits | Where-Object { $_.Current.Name -match '文件名|File name' }
        $edits
    )
    foreach ($edit in $candidates) {
        try {
            $value = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $value.SetValue($Path)
            return
        }
        catch {
            continue
        }
    }
    throw '未找到文件选择框中的文件名输入框。'
}

function Find-OpenDialog {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    foreach ($window in @($desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $windowCondition))) {
        try {
            $name = $window.Current.Name
            if ($window.Current.ProcessId -eq $ProcessId -and
                $name -match '打开|Open|选择|Select' -and
                $name -notmatch '保存|Save|导出|Export') {
                return $window
            }
        }
        catch {
            continue
        }
    }
    return $null
}

function Wait-OpenDialog {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [int]$Seconds = $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $dialog = Find-OpenDialog -ProcessId $ProcessId
        if ($null -ne $dialog) {
            return $dialog
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Wait-FileDialogForeground {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$MainWindowHandle,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [int]$Seconds = $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $consoleHandle = [ShineLabRpa.NativeMethods]::GetConsoleWindowHandle()
    while ([DateTime]::UtcNow -lt $deadline) {
        $foreground = [ShineLabRpa.NativeMethods]::GetForegroundWindowHandle()
        if ($foreground -ne [IntPtr]::Zero -and
            $foreground -ne $MainWindowHandle -and
            $foreground -ne $consoleHandle -and
            [ShineLabRpa.NativeMethods]::IsOpenFileDialogWindow($foreground, $ProcessId)) {
            return $foreground
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Wait-WindowClosed {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$Handle,
        [int]$Seconds = 5
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not [ShineLabRpa.NativeMethods]::IsWindowHandle($Handle)) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return -not [ShineLabRpa.NativeMethods]::IsWindowHandle($Handle)
}

$csv = Read-ShineCsv -Path $CsvPath
Write-Stage "脚本版本：$script:RpaScriptVersion"
Write-Stage "CSV校验通过：$($csv.Count) 条任务，文件 $($csv.Path)"

if ($ExpectedRows -gt 0 -and $csv.Count -ne $ExpectedRows) {
    throw "CSV任务数为 $($csv.Count)，与期望值 $ExpectedRows 不一致。"
}

if ($ProbeContextMenu -and $ExecuteImport) {
    throw '菜单探针模式与正式导入互斥。使用 -ProbeContextMenu 时不要指定 -ExecuteImport。'
}

if (-not $ExecuteImport -and -not $ProbeContextMenu) {
    Write-Stage '当前为校验模式。未启动或操作 ShineLab。'
    Write-Stage '执行导入需要同时指定 -ExecuteImport -AllowAppend；脚本不会点击“运行”。'
    exit 0
}

if ($ExecuteImport -and -not $AllowAppend) {
    throw 'ShineLab 导入是追加操作。请显式指定 -AllowAppend 后再执行，避免误产生重复任务。'
}

if (($null -eq $ImportMenuPointX) -xor ($null -eq $ImportMenuPointY)) {
    throw '菜单点击坐标必须同时提供 -ImportMenuPointX 和 -ImportMenuPointY。'
}
$useImportMenuPoint = $null -ne $ImportMenuPointX -and $null -ne $ImportMenuPointY

Add-UiAutomationAssemblies
$process = Get-MainWindowProcess -ExecutablePath $ShineLabPath
$process.Refresh()
[ShineLabRpa.NativeMethods]::Activate($process.MainWindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

$analysisTab = Find-ElementByNames -Root $window -Names $script:AnalysisTabNames
if ($null -ne $analysisTab) {
    Invoke-UiElement -Element $analysisTab
    Start-Sleep -Milliseconds 500
}

$grid = Find-TaskGrid -Window $window
$beforeCount = $null
if ($null -ne $grid) {
    $beforeCount = Get-TaskRowCount -Grid $grid
    Write-Stage "已定位任务表，导入前可识别行数：$beforeCount"
}

if ($null -ne $GridPointX -and $null -ne $GridPointY) {
    $gridPoint = [pscustomobject]@{ X = [int]$GridPointX; Y = [int]$GridPointY }
}
elseif ($null -ne $grid) {
    $gridPoint = Get-ElementPoint -Element $grid
}
else {
    throw '未定位到样品任务表。请先在分析控制中创建或打开序列，或提供 -GridPointX/-GridPointY。'
}

$consoleMinimized = $false
$menuBeforePath = Join-Path (Split-Path -Parent $csv.Path) 'ShineLab-menu-before.tmp.png'
$menuEvidencePath = Join-Path (Split-Path -Parent $csv.Path) 'ShineLab-menu-probe.png'
try {
    # Coordinate-based automation must not click through the PowerShell console.
    if (-not $KeepConsoleVisible) {
        [ShineLabRpa.NativeMethods]::MinimizeConsole()
        $consoleMinimized = $true
        Start-Sleep -Milliseconds 500
    }
    $activated = [ShineLabRpa.NativeMethods]::ActivateAndVerify(
        $process.MainWindowHandle,
        $process.Id)
    Start-Sleep -Milliseconds 300
    $foregroundHandle = [ShineLabRpa.NativeMethods]::GetForegroundWindowHandle()
    if (-not $activated -or
        -not [ShineLabRpa.NativeMethods]::IsWindowInProcess($foregroundHandle, $process.Id)) {
        $foregroundDescription = [ShineLabRpa.NativeMethods]::DescribeWindow($foregroundHandle)
        throw ('无法将 ShineLab 切换到前台，未执行坐标右键。请关闭或最小化记事本等遮挡窗口后重试。当前前台窗口：{0}' -f $foregroundDescription)
    }
    Write-Stage "已确认 ShineLab 位于前台：$([ShineLabRpa.NativeMethods]::DescribeWindow($foregroundHandle))"

    $pointWindowHandle = [ShineLabRpa.NativeMethods]::GetWindowAtPoint(
        $gridPoint.X,
        $gridPoint.Y)
    if (-not [ShineLabRpa.NativeMethods]::IsWindowInProcess($pointWindowHandle, $process.Id)) {
        $pointWindowDescription = [ShineLabRpa.NativeMethods]::DescribeWindow($pointWindowHandle)
        throw ('任务表坐标没有命中 ShineLab 窗口，未执行右键。坐标：{0},{1}；命中窗口：{2}' -f $gridPoint.X, $gridPoint.Y, $pointWindowDescription)
    }
    Write-Stage "任务表坐标命中窗口：$([ShineLabRpa.NativeMethods]::DescribeWindow($pointWindowHandle))"

    Save-DesktopScreenshot -Path $menuBeforePath
    Write-Stage "向任务表发送右键输入：$($gridPoint.X),$($gridPoint.Y)"
    [ShineLabRpa.NativeMethods]::RightClick($gridPoint.X, $gridPoint.Y)
    $physicalCursor = [ShineLabRpa.NativeMethods]::GetPhysicalCursorPosition()
    $physicalCursorX = [int]$physicalCursor[0]
    $physicalCursorY = [int]$physicalCursor[1]
    Write-Stage ('右键输入后的实际物理光标：{0},{1}' -f $physicalCursorX, $physicalCursorY)
    Start-Sleep -Milliseconds 600
    Save-DesktopScreenshot -Path $menuEvidencePath
    $menuDifference = Compare-ScreenshotRegion `
        -BeforePath $menuBeforePath `
        -AfterPath $menuEvidencePath `
        -CenterX $physicalCursorX `
        -CenterY $physicalCursorY
    Write-Stage ('右键前后界面变化：采样点 {0}，范围 {1}' -f $menuDifference.ChangedSamples, $menuDifference.Bounds)
    if ($menuDifference.ChangedSamples -lt 80) {
        [ShineLabRpa.NativeMethods]::DismissContextMenu()
        throw ('未检测到右键菜单所需的界面变化，已停止且未点击菜单。证据截图：{0}' -f $menuEvidencePath)
    }
    Write-Stage "右键结果校验通过，当前证据截图：$menuEvidencePath"

    if ($ProbeContextMenu) {
        Write-Stage '菜单将保留 5 秒供现场观察。'
        Start-Sleep -Seconds 5
        [ShineLabRpa.NativeMethods]::DismissContextMenu()
        Write-Stage '菜单探针完成：未选择菜单、未打开文件框、未导入任务。'
        return
    }
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $menuWaitSeconds = if ($UseKeyboardMenuFallback -or $useImportMenuPoint) { 2 } else { $TimeoutSeconds }
    $importMenu = Wait-ElementByNames -Root $desktop -Names $script:ImportMenuNames -Seconds $menuWaitSeconds
    if ($null -ne $importMenu) {
        Write-Stage '已通过 UI Automation 定位“从CSV导入”。'
        Invoke-UiElement -Element $importMenu
    }
    else {
        $nativeMenu = [ShineLabRpa.NativeMethods]::TryClickPopupMenuItem(
            $process.Id,
            [string[]]$script:ImportMenuNames,
            $gridPoint.X,
            $gridPoint.Y)
        if ($nativeMenu.Clicked) {
            Write-Stage "已校验 Win32 菜单文本并点击：$($nativeMenu.ItemText)"
        }
        elseif ($useImportMenuPoint) {
            $menuPointWindowHandle = [ShineLabRpa.NativeMethods]::GetWindowAtPoint(
                [int]$ImportMenuPointX,
                [int]$ImportMenuPointY)
            if (-not [ShineLabRpa.NativeMethods]::IsWindowInProcess($menuPointWindowHandle, $process.Id)) {
                $menuPointWindowDescription = [ShineLabRpa.NativeMethods]::DescribeWindow($menuPointWindowHandle)
                throw ('导入菜单坐标没有命中 ShineLab 窗口，未执行左键。坐标：{0},{1}；命中窗口：{2}' -f $ImportMenuPointX, $ImportMenuPointY, $menuPointWindowDescription)
            }
            Write-Stage ('按探针截图的物理坐标点击末项“从CSV导入”：{0},{1}' -f $ImportMenuPointX, $ImportMenuPointY)
            $actualMenuClickPoint = [ShineLabRpa.NativeMethods]::LeftClickPhysical(
                [int]$ImportMenuPointX,
                [int]$ImportMenuPointY)
            Write-Stage "菜单左键的实际物理光标：$actualMenuClickPoint"
        }
        elseif ($UseKeyboardMenuFallback -and
            [ShineLabRpa.NativeMethods]::IsWindowInProcess(
                [ShineLabRpa.NativeMethods]::GetForegroundWindowHandle(),
                $process.Id)) {
            if ($nativeMenu.PopupDetected) {
                Write-Stage "已检测到 ShineLab 自绘弹出菜单，但菜单未提供可读取文本（$($nativeMenu.Diagnostic)）。"
            }
            else {
                Write-Stage "ShineLab 自绘菜单未提供可枚举窗口（$($nativeMenu.Diagnostic)）。"
            }
            Write-Stage '使用已确认的两项菜单顺序：按 End 键选择末项“从CSV导入”。'
            # The verified menu order is 导出CSV, 从CSV导入.
            [ShineLabRpa.NativeMethods]::SelectLastContextMenuItem()
        }
        elseif ($UseKeyboardMenuFallback) {
            $foregroundDescription = [ShineLabRpa.NativeMethods]::DescribeWindow(
                [ShineLabRpa.NativeMethods]::GetForegroundWindowHandle())
            throw ('右键后未检测到 ShineLab 弹出菜单，未发送菜单按键。请确认坐标位于任务表内部且没有窗口遮挡。菜单诊断：{0}；当前前台窗口：{1}' -f $nativeMenu.Diagnostic, $foregroundDescription)
        }
        else {
            throw ('未找到“从CSV导入”菜单。Win32诊断：{0}' -f $nativeMenu.Diagnostic)
        }
    }

    $dialogWaitSeconds = if ($UseKeyboardFileDialogFallback) { 2 } else { $TimeoutSeconds }
    $dialog = Wait-OpenDialog -ProcessId $process.Id -Seconds $dialogWaitSeconds
    if ($null -ne $dialog) {
        $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
        Write-Stage "已检测到 ShineLab 导入文件窗口：$($dialog.Current.Name)"
        Set-FileDialogPath -Dialog $dialog -Path $csv.Path
        $openButton = Find-ElementByNames -Root $dialog -Names $script:OpenButtonNames
        if ($null -eq $openButton) {
            throw '未找到文件选择框的“打开”按钮。'
        }
        Invoke-UiElement -Element $openButton
        if ($dialogHandle -ne [IntPtr]::Zero -and -not (Wait-WindowClosed -Handle $dialogHandle)) {
            throw '导入文件窗口仍未关闭，CSV 文件名可能未写入正确的输入框。'
        }
    }
    elseif ($UseKeyboardFileDialogFallback) {
        Write-Stage '文件窗口未暴露给 UI Automation，检查 ShineLab 前台原生“打开”窗口。'
        $dialogHandle = Wait-FileDialogForeground `
            -MainWindowHandle $process.MainWindowHandle `
            -ProcessId $process.Id `
            -Seconds $TimeoutSeconds
        if ($dialogHandle -eq [IntPtr]::Zero) {
            $foregroundDescription = [ShineLabRpa.NativeMethods]::DescribeWindow(
                [ShineLabRpa.NativeMethods]::GetForegroundWindowHandle())
            throw ('未检测到 ShineLab 的“打开/选择文件”窗口；很可能没有触发“从CSV导入”。当前前台窗口：{0}' -f $foregroundDescription)
        }
        Write-Stage "已检测到原生导入文件窗口：$([ShineLabRpa.NativeMethods]::DescribeWindow($dialogHandle))"
        try {
            Set-Clipboard -Value $csv.Path -ErrorAction Stop
        }
        catch {
            throw '无法写入剪贴板，无法执行文件选择框键盘兜底。'
        }

        [ShineLabRpa.NativeMethods]::Activate($dialogHandle)
        [ShineLabRpa.NativeMethods]::FocusNativeFileName()
        [ShineLabRpa.NativeMethods]::PasteAndOpenNativeFile()
        if (-not (Wait-WindowClosed -Handle $dialogHandle)) {
            throw '文件选择框仍未关闭，CSV路径未被确认；未报告导入成功。'
        }
    }
    else {
        throw '未找到 CSV 文件选择框。'
    }
    Write-Stage '已提交 CSV 导入，等待 ShineLab 刷新任务表。'
    Start-Sleep -Milliseconds 800

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $afterCount = $null
    $rowsVerified = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $grid = Find-TaskGrid -Window $window
        if ($null -ne $grid) {
            $afterCount = Get-TaskRowCount -Grid $grid
            if ($null -ne $beforeCount -and $null -ne $afterCount -and $afterCount -ge ($beforeCount + $csv.Count)) {
                break
            }
        }
        Start-Sleep -Milliseconds 300
    }

    if ($null -ne $beforeCount -and $null -ne $afterCount) {
        $expectedAfter = $beforeCount + $csv.Count
        if ($afterCount -lt $expectedAfter) {
            throw "导入后任务行数为 $afterCount，至少应为 $expectedAfter。请检查 ShineLab 是否完成刷新。"
        }
        $rowsVerified = $true
        Write-Stage "导入校验通过：$beforeCount + $($csv.Count) = $afterCount（追加模式）。"
    }
    else {
        Write-Warning 'UI Automation 未能可靠读取任务行数。请人工确认任务数量和样品内容后再运行。'
        Write-Stage 'RPA 已提交导入动作，但当前版本未能自动验证任务行数；不能将此结果视为导入成功。'
    }

    if ($rowsVerified) {
        Write-Stage 'RPA 导入步骤完成。脚本未点击“运行”，请人工确认后执行。'
    }
}
finally {
    if ([System.IO.File]::Exists($menuBeforePath)) {
        [System.IO.File]::Delete($menuBeforePath)
    }
    if ($consoleMinimized) {
        [ShineLabRpa.NativeMethods]::RestoreConsole()
    }
}










