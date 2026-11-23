using System.Runtime.InteropServices;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;

namespace InkCanvas.App.Services;

/// <summary>Shell image picker on its own STA so shell startup never blocks or disables the canvas.</summary>
internal static class ImageFilePicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint CancelMessage = 0x8000 + 118;
    private static int _active;
    private static IntPtr _owner;
    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    public static Task<string> PickAsync(ScreenRect workArea, CancellationToken token)
        => PickAsync(workArea, ImageImport.SupportedExtensions, "插入图片", "图片文件", token);

    public static Task<string> PickAsync(ScreenRect workArea, IReadOnlyList<string> extensions, string title, string filterName, CancellationToken token)
        => StartPicker(workArea, extensions, title, filterName, token);

    public static Task<string> SaveArchiveAsync(Windows.Graphics.RectInt32 workArea, string directory, string name, CancellationToken token = default)
        => StartPicker(new(workArea.X, workArea.Y, workArea.Width, workArea.Height), new[] { ".icink" }, "保存 Ink Canvas 存档", "Ink Canvas 存档", token, directory, name, save: true);

    public static Task<string> PickFolderAsync(Windows.Graphics.RectInt32 workArea, string directory, CancellationToken token = default)
        => StartPicker(new(workArea.X, workArea.Y, workArea.Width, workArea.Height), Array.Empty<string>(), "选择存档目录", "", token, directory, folder: true);

    public static Task<string> OpenArchiveAsync(Windows.Graphics.RectInt32 workArea, string directory, IReadOnlyList<string> extensions, CancellationToken token = default)
        => StartPicker(new(workArea.X, workArea.Y, workArea.Width, workArea.Height), extensions, "打开 Ink Canvas 存档", "画板与导入文件", token, directory);

    private static Task<string> StartPicker(ScreenRect workArea, IReadOnlyList<string> extensions, string title, string filterName,
        CancellationToken token, string directory = null, string name = null, bool save = false, bool folder = false)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return Task.FromResult<string>(null);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(workArea, extensions, title, filterName, token, completion, directory, name, save, folder)) { IsBackground = true, Name = "InkCanvas file picker" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch (Exception ex) { Interlocked.Exchange(ref _active, 0); completion.TrySetException(ex); }
        return completion.Task;
    }

    public static void EnsureAbove(IntPtr drawingWindow)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner == IntPtr.Zero || !NativeMethods.IsWindowVisible(drawingWindow)) return;
        var popup = GetLastActivePopup(owner);
        if (popup == owner || !NativeMethods.IsWindowVisible(popup)) return;
        for (var above = NativeMethods.GetWindow(popup, 3); above != IntPtr.Zero; above = NativeMethods.GetWindow(above, 3))
        {
            if (above != drawingWindow) continue;
            NativeMethods.SetWindowPos(popup, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | 0x4000 /* ASYNCWINDOWPOS */);
            break;
        }
    }

    private static void Run(ScreenRect work, IReadOnlyList<string> extensions, string title, string filterName, CancellationToken token,
        TaskCompletionSource<string> completion, string directory, string name, bool save, bool folder)
    {
        IntPtr owner = IntPtr.Zero;
        IFileDialog dialog = null;
        string path = null;
        Exception error = null;
        SubclassProc procedure = (window, message, wParam, lParam, id, data) =>
        {
            if (message == CancelMessage || message == 0x0113 /* WM_TIMER */ && token.IsCancellationRequested)
            {
                try { dialog?.Close(Cancelled); } catch (COMException) { }
                return IntPtr.Zero;
            }
            return DefSubclassProc(window, message, wParam, lParam);
        };
        try
        {
            token.ThrowIfCancellationRequested();
            // Only this invisible helper is disabled by Show. The real drawing windows remain enabled.
            owner = NativeMethods.CreateWindowExW(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST,
                "STATIC", "Ink Canvas " + title, NativeMethods.WS_POPUP,
                work.X + work.Width / 2, work.Y + work.Height / 2, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (owner == IntPtr.Zero || !SetWindowSubclass(owner, procedure, 1, 0))
                throw new InvalidOperationException("无法创建文件选择窗口。");
            Volatile.Write(ref _owner, owner);
            using var registration = token.Register(() =>
            {
                PostMessageW(owner, CancelMessage, IntPtr.Zero, IntPtr.Zero);
                var popup = GetLastActivePopup(owner);
                if (popup != IntPtr.Zero && popup != owner) PostMessageW(popup, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            });
            dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(save
                ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true));
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | 0x40 /* FORCEFILESYSTEM */ | (save ? 0x2u /* OVERWRITEPROMPT */ : folder ? 0 : 0x1000u /* FILEMUSTEXIST */)
                | (folder ? 0x20u /* PICKFOLDERS */ : 0) | 0x800 /* PATHMUSTEXIST */ | 0x8 /* NOCHANGEDIR */);
            dialog.SetTitle(title);
            if (!folder) dialog.SetFileTypes(1, new[] { new FilterSpec { Name = filterName, Pattern = string.Join(';', extensions.Select(extension => "*" + extension)) } });
            if (save) { dialog.SetFileName(Path.GetFileNameWithoutExtension(name)); dialog.SetDefaultExtension("icink"); }
            bool video = filterName == "视频文件";
            var clientId = new Guid(directory is not null
                ? "71ED3AE4-9253-4CBC-8925-CDE0744013E1"
                : video ? "B21DB3DB-7994-4752-9124-132F0A3B659B" : "0C64EAE6-C610-4F23-974F-290DD5B6F12D");
            dialog.SetClientGuid(ref clientId);
            string pictures = directory ?? Environment.GetFolderPath(video ? Environment.SpecialFolder.MyVideos : Environment.SpecialFolder.MyPictures);
            if (save) Directory.CreateDirectory(pictures);
            if (Directory.Exists(pictures))
            {
                Guid itemId = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(pictures, IntPtr.Zero, ref itemId, out var folderItem) >= 0)
                {
                    try { if (directory is null) dialog.SetDefaultFolder(folderItem); else dialog.SetFolder(folderItem); }
                    finally { Marshal.ReleaseComObject(folderItem); }
                }
            }
            token.ThrowIfCancellationRequested();
            SetTimer(owner, 1, 100, IntPtr.Zero);
            int result = dialog.Show(owner);
            if (result != Cancelled)
            {
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out var item);
                try { item.GetDisplayName(0x80058000 /* FILESYSPATH */, out path); }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            Volatile.Write(ref _owner, IntPtr.Zero);
            if (owner != IntPtr.Zero) KillTimer(owner, 1);
            var completedDialog = dialog;
            dialog = null;
            if (completedDialog is not null) Marshal.ReleaseComObject(completedDialog);
            if (owner != IntPtr.Zero)
            {
                RemoveWindowSubclass(owner, procedure, 1);
                NativeMethods.DestroyWindow(owner);
            }
            GC.KeepAlive(procedure);
            Interlocked.Exchange(ref _active, 0);
        }
        if (token.IsCancellationRequested) completion.TrySetCanceled(token);
        else if (error is not null) completion.TrySetException(error);
        else completion.TrySetResult(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid([In] ref Guid guid);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr window, nuint id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, nuint id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
}
