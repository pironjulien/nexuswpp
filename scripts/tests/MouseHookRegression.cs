using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Exercises the built host without starting its wallpaper or injecting user input.
internal static class MouseHookRegression
{
    const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static Type host;

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }
    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    sealed class Receiver : NativeWindow, IDisposable
    {
        public readonly List<string> Messages = new List<string>();
        public Receiver()
        {
            CreateHandle(new CreateParams { Caption = "NexusWpp input regression", Width = 200, Height = 100 });
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x201 || message.Msg == 0x202)
            {
                long coordinates = message.LParam.ToInt64();
                Messages.Add(message.Msg + ":" + unchecked((short)coordinates) + ":" + unchecked((short)(coordinates >> 16)));
            }
            base.WndProc(ref message);
        }
        public void Dispose() { DestroyHandle(); }
    }

    static object Invoke(string method, params object[] args)
    {
        return host.GetMethod(method, PrivateStatic).Invoke(null, args);
    }
    static object Field(string name) { return host.GetField(name, PrivateStatic).GetValue(null); }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    static bool WaitUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 2000) Thread.Sleep(1);
        return condition();
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            host = Assembly.LoadFrom(args[0]).GetType("DesktopHtmlHost.DesktopForm", true);
            // These paths must not dereference a pointer or access a UI control.
            Check((IntPtr)Invoke("HookCallback", -1, (IntPtr)0x201, IntPtr.Zero) == IntPtr.Zero, "negative hook code passes through");
            Check((IntPtr)Invoke("HookCallback", 0, (IntPtr)0x200, IntPtr.Zero) == IntPtr.Zero, "motion passes through without reading event data");
            Check((IntPtr)Invoke("HookCallback", 0, (IntPtr)0x20A, IntPtr.Zero) == IntPtr.Zero, "wheel passes through without reading event data");
            Check((IntPtr)Invoke("HookCallback", 0, (IntPtr)0x201, IntPtr.Zero) == IntPtr.Zero, "absent routing does not swallow a click");

            IntPtr destroyed;
            using (Receiver receiver = new Receiver())
            {
                destroyed = receiver.Handle;
                foreach (Point local in new[] { new Point { X = 23, Y = 37 }, new Point { X = -9, Y = -7 } })
                {
                    Point screen = local;
                    Check(ClientToScreen(receiver.Handle, ref screen), "test receiver screen coordinates");
                    Check((bool)Invoke("ForwardMouseClick", receiver.Handle, screen.X, screen.Y, (uint)0x201), "button down accepted by native target");
                    Check((bool)Invoke("ForwardMouseClick", receiver.Handle, screen.X, screen.Y, (uint)0x202), "button up accepted by native target");
                }
                Check(WaitUntil(delegate { Application.DoEvents(); return receiver.Messages.Count == 4; }), "posted clicks delivered without synchronous UI invocation");
                Check(string.Join(",", receiver.Messages.ToArray()) == "513:23:37,514:23:37,513:-9:-7,514:-9:-7", "click order and signed client coordinates preserved");
            }
            Check(!(bool)Invoke("ForwardMouseClick", destroyed, 0, 0, (uint)0x201), "destroyed target does not swallow a click");

            for (int cycle = 0; cycle < 10; cycle++)
            {
                Invoke("StartMouseHook");
                Check(WaitUntil(delegate { return (IntPtr)Field("hookId") != IntPtr.Zero; }), "native hook installed");
                Check(unchecked((uint)(int)Field("mouseHookThreadId")) != GetCurrentThreadId(), "hook serviced independently of caller UI thread");
                Thread hookThread = (Thread)Field("mouseHookThread");
                Invoke("StopMouseHook");
                Check(hookThread.Join(2000) && (IntPtr)Field("hookId") == IntPtr.Zero, "hook removed and message pump stopped");
            }
            for (int cycle = 0; cycle < 10; cycle++)
            {
                Invoke("StartMouseHook");
                Thread hookThread = (Thread)Field("mouseHookThread");
                Invoke("StopMouseHook");
                Check(hookThread.Join(2000) && (IntPtr)Field("hookId") == IntPtr.Zero, "immediate shutdown cannot strand a hook");
            }
            Check(Field("mouseHookError") == null, "no callback or native lifecycle errors");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (host != null) Invoke("StopMouseHook"); }
    }
}
