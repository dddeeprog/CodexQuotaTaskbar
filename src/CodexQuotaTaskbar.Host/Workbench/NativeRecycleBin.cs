using System.IO;
using System.Runtime.InteropServices;

namespace CodexQuotaTaskbar.Host.Workbench;

// IFileOperation is in-process. The progress sink vetoes every non-recycling delete;
// errors and cancellation never fall back to File.Delete/Directory.Delete.
internal sealed class NativeRecycleBin : IWorkspaceRecycleBin
{
    internal static bool IsRecycling(uint flags) => (flags & 0x80) != 0;
    public void Recycle(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new IOException("此位置不支持工作台回收站操作，请使用资源管理器处理。");
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
        IShellItem? item = null;
        try
        {
            var id = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, 0, ref id, out item);
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0010 | 0x0004);
            var sink = new RecycleSink();
            operation.DeleteItem(item, sink); operation.PerformOperations(); operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || sink.Result < 0 || !sink.Recycled) throw new IOException("项目未移入回收站，操作已停止。", sink.Result);
        }
        finally { if (item is not null) Marshal.FinalReleaseComObject(item); Marshal.FinalReleaseComObject(operation); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint context, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(uint hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations(); [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink : IFileOperationProgressSink
    {
        internal int Result; internal bool Recycled;
        public int StartOperations() => 0; public int FinishOperations(int result) { if (result < 0) Result = result; return 0; }
        public int PreDeleteItem(uint flags, IShellItem item) => IsRecycling(flags) ? 0 : unchecked((int)0x80004004);
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) { Result = result; Recycled = result >= 0 && created is not null; return 0; }
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0; public int ResetTimer() => 0; public int PauseTimer() => 0; public int ResumeTimer() => 0;
    }
}
