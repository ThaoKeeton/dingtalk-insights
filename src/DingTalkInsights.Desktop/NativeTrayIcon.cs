using System.Runtime.InteropServices;

namespace DingTalkInsights.Desktop;

internal interface INotificationArea
{
    bool Add(IntPtr window,uint callback);
    bool Probe(IntPtr window);
    void Delete(IntPtr window);
}
internal sealed class WindowsNotificationArea:INotificationArea
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct NotifyIconData
    {
        public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    static NotifyIconData Data(IntPtr window)=>new(){Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=window,Id=1,Tip="DingTalk Insights · 打开 / 停止 / 退出",Info="",InfoTitle=""};
    public bool Add(IntPtr window,uint callback)
    {
        var data=Data(window);data.Flags=1|2|4;data.Callback=callback;data.Icon=LoadIconW(IntPtr.Zero,new IntPtr(32512));
        if(data.Icon==IntPtr.Zero||!Shell_NotifyIconW(0,ref data))return false;
        data.Version=4;
        if(Shell_NotifyIconW(4,ref data))return true;
        Delete(window);return false;
    }
    public bool Probe(IntPtr window){var data=Data(window);data.Flags=4;return Shell_NotifyIconW(1,ref data);}
    public void Delete(IntPtr window){var data=Data(window);Shell_NotifyIconW(2,ref data);}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)][return:MarshalAs(UnmanagedType.Bool)]static extern bool Shell_NotifyIconW(uint message,ref NotifyIconData data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr LoadIconW(IntPtr instance,IntPtr name);
}

/// <summary>Window subclass owns the restore path. Icon loss always restores the main window.</summary>
internal sealed class NativeTrayIcon:IDisposable
{
    const uint Callback=0x8001;readonly IntPtr window;readonly INotificationArea area;
    readonly Action show,stop,exit,wake;readonly SubclassProc callback;readonly uint taskbarCreated;
    bool attached,healthy,disposed;
    public bool IsHealthy=>!disposed&&healthy;
    public NativeTrayIcon(IntPtr window,Action show,Action stop,Action exit,Action wake,INotificationArea? area=null)
    {
        this.window=window;this.show=show;this.stop=stop;this.exit=exit;this.wake=wake;this.area=area??new WindowsNotificationArea();callback=WindowProc;
        taskbarCreated=RegisterWindowMessageW("TaskbarCreated");
        attached=taskbarCreated!=0&&SetWindowSubclass(window,callback,new UIntPtr(1),UIntPtr.Zero);
        if(attached)healthy=this.area.Add(window,Callback);
    }
    public bool CanHide()
    {
        healthy=healthy&&area.Probe(window);
        if(!healthy)show();return healthy;
    }
    public void ReRegister()
    {
        if(disposed)return;area.Delete(window);healthy=attached&&area.Add(window,Callback);
        if(!healthy)show();
    }
    IntPtr WindowProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(message==taskbarCreated){ReRegister();return IntPtr.Zero;}
        if(message==0x218&&(wParam.ToUInt64() is 7 or 18)){wake();}
        if(message==Callback)
        {
            var action=(uint)(lParam.ToInt64()&0xffff);
            if(action is 0x202 or 0x203 or 0x400 or 0x401)show();
            else if(action is 0x205 or 0x7b)ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd,message,wParam,lParam);
    }
    void ShowMenu()
    {
        var menu=CreatePopupMenu();if(menu==IntPtr.Zero){show();return;}
        try
        {
            AppendMenuW(menu,0,1,"打开主窗口");AppendMenuW(menu,0,2,"停止监控并撤销重启恢复");AppendMenuW(menu,0,3,"退出应用");
            if(!GetCursorPos(out var point)){show();return;}
            SetForegroundWindow(window);
            var choice=TrackPopupMenu(menu,0x100|0x2,point.X,point.Y,0,window,IntPtr.Zero);
            PostMessageW(window,0,UIntPtr.Zero,IntPtr.Zero);
            HandleCommand(choice);
        }
        finally{DestroyMenu(menu);}
    }
    internal void HandleCommand(uint command){if(command==1)show();else if(command==2)stop();else if(command==3)exit();}
    public void Dispose()
    {
        if(disposed)return;disposed=true;area.Delete(window);healthy=false;
        if(attached)RemoveWindowSubclass(window,callback,new UIntPtr(1));attached=false;
    }
    [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
    delegate IntPtr SubclassProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool SetWindowSubclass(IntPtr window,SubclassProc proc,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool RemoveWindowSubclass(IntPtr window,SubclassProc proc,UIntPtr id);
    [DllImport("comctl32.dll")]static extern IntPtr DefSubclassProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll")]static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)][return:MarshalAs(UnmanagedType.Bool)]static extern bool AppendMenuW(IntPtr menu,uint flags,uint id,string text);
    [DllImport("user32.dll")]static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr window,IntPtr rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)][return:MarshalAs(UnmanagedType.Bool)]static extern bool PostMessageW(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
}
