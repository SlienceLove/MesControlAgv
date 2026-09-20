using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;

internal static class NativePresentationKeyboard
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, nint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a,uint b,bool attach);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,nuint extra);

    public static async Task FocusAsync(Window window, WebView2 browser)
    {
        var own=GetCurrentThreadId(); var foreground=GetWindowThreadProcessId(GetForegroundWindow(),0);
        var attached=own!=foreground && AttachThreadInput(own,foreground,true);
        try { SetForegroundWindow(new WindowInteropHelper(window).Handle); window.Activate(); }
        finally { if(attached) AttachThreadInput(own,foreground,false); }
        browser.Focus();
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(await browser.CoreWebView2.ExecuteScriptAsync("document.hasFocus()")!="true")
        {if(DateTime.UtcNow>deadline)throw new InvalidOperationException("Offline WebView did not get keyboard focus; no input sent");await Task.Delay(50);}
    }
    public static void Key(Window window,byte key)
    {
        if(key is not (27 or 122))throw new ArgumentException("Only display Esc/F11 are allowed");
        if(GetForegroundWindow()!=new WindowInteropHelper(window).Handle ||
            new[]{16,17,18,91,92}.Any(modifier=>(GetAsyncKeyState(modifier)&0x8000)!=0))
            throw new InvalidOperationException("Test window lost focus or modifier held; no input sent");
        keybd_event(key,0,0,0);keybd_event(key,0,2,0);
    }
}
