using System;
using System.IO;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

class ClipboardInterceptor : NativeWindow, IDisposable {
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly string TargetDir = Path.Combine(Path.GetTempPath(), "ClipboardInterceptor");

    public ClipboardInterceptor() {
        try {
            if (!Directory.Exists(TargetDir)) Directory.CreateDirectory(TargetDir);
            ThreadPool.QueueUserWorkItem(new WaitCallback(state => CleanupOldFiles()));
        } catch { }

        CreateParams cp = new CreateParams();
        cp.Parent = (IntPtr)(-3); // HWND_MESSAGE
        this.CreateHandle(cp);
        
        AddClipboardFormatListener(this.Handle);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_CLIPBOARDUPDATE) {
            uint pngFormatId = RegisterClipboardFormat("PNG");
            
            bool hasPng = IsClipboardFormatAvailable(pngFormatId);
            bool hasBitmap = IsClipboardFormatAvailable(2);  // CF_BITMAP
            bool hasFiles = IsClipboardFormatAvailable(15);  // CF_HDROP

            if ((hasPng || hasBitmap) && !hasFiles) {
                ProcessClipboardContent(pngFormatId);
            }
        }
        base.WndProc(ref m);
    }

    private void ProcessClipboardContent(uint pngFormatId) {
        string uniqueId = Guid.NewGuid().ToString("N");
        string fileName = string.Format("scr_{0:yyyyMMdd_HHmmss_fff}_{1}.png", DateTime.Now, uniqueId);
        string fullPath = Path.Combine(TargetDir, fileName);

        bool saved = false;
        byte[]? pngBytes = null;
        byte[]? dibBytes = null;

        // Чтение исходных данных
        bool openedForRead = false;
        for (int i = 0; i < 10; i++) {
            if (OpenClipboard(this.Handle)) {
                openedForRead = true;
                break;
            }
            Thread.Sleep(5);
        }

        if (!openedForRead) return;

        try {
            if (IsClipboardFormatAvailable(pngFormatId)) {
                pngBytes = GetClipboardBytes(pngFormatId);
                if (pngBytes != null) {
                    try {
                        File.WriteAllBytes(fullPath, pngBytes);
                        saved = true;
                    } catch { }
                }
            }

            if (!saved && IsClipboardFormatAvailable(2)) {
                IntPtr hBitmap = GetClipboardData(2);
                if (hBitmap != IntPtr.Zero) {
                    try {
                        using (Bitmap bmp = Bitmap.FromHbitmap(hBitmap)) {
                            bmp.Save(fullPath, ImageFormat.Png);
                            saved = true;
                            using (MemoryStream ms = new MemoryStream()) {
                                bmp.Save(ms, ImageFormat.Png);
                                pngBytes = ms.ToArray();
                            }
                        }
                    } catch { }
                }
            }

            if (IsClipboardFormatAvailable(8)) {
                dibBytes = GetClipboardBytes(8);
            }
        } finally {
            CloseClipboard();
        }

        // Нативная запись
        if (saved) {
            bool openedForWrite = false;
            for (int i = 0; i < 15; i++) {
                if (OpenClipboard(this.Handle)) {
                    openedForWrite = true;
                    break;
                }
                Thread.Sleep(5);
            }

            if (!openedForWrite) return;

            try {
                EmptyClipboard();

                // 1. Ссылка на файл (CF_HDROP)
                IntPtr hDrop = CreateHDropBuffer(fullPath);
                if (hDrop != IntPtr.Zero) {
                    if (SetClipboardData(15, hDrop) == IntPtr.Zero) GlobalFree(hDrop);
                }

                // 2. Чистый PNG массив
                if (pngBytes != null) {
                    IntPtr hPng = CreateGlobalBuffer(pngBytes);
                    if (hPng != IntPtr.Zero) {
                        if (SetClipboardData(pngFormatId, hPng) == IntPtr.Zero) GlobalFree(hPng);
                    }
                }

                // 3. Структура DIB
                if (dibBytes != null) {
                    IntPtr hDib = CreateGlobalBuffer(dibBytes);
                    if (hDib != IntPtr.Zero) {
                        if (SetClipboardData(8, hDib) == IntPtr.Zero) GlobalFree(hDib);
                    }
                } else if (pngBytes != null) {
                    try {
                        using (MemoryStream ms = new MemoryStream(pngBytes))
                        using (Bitmap bmp = new Bitmap(ms)) {
                            IntPtr hBitmap = bmp.GetHbitmap();
                            if (hBitmap != IntPtr.Zero) {
                                if (SetClipboardData(2, hBitmap) == IntPtr.Zero) DeleteObject(hBitmap);
                            }
                        }
                    } catch { }
                }

                // 4. Метки имени файла для Ditto
                uint formatFileNameW = RegisterClipboardFormat("FileNameW");
                IntPtr hNameW = CreateGlobalBuffer(Encoding.Unicode.GetBytes(fullPath + "\0"));
                if (hNameW != IntPtr.Zero) {
                    if (SetClipboardData(formatFileNameW, hNameW) == IntPtr.Zero) GlobalFree(hNameW);
                }

                uint formatFileName = RegisterClipboardFormat("FileName");
                IntPtr hNameA = CreateGlobalBuffer(Encoding.Default.GetBytes(fullPath + "\0"));
                if (hNameA != IntPtr.Zero) {
                    if (SetClipboardData(formatFileName, hNameA) == IntPtr.Zero) GlobalFree(hNameA);
                }
            } finally {
                CloseClipboard();
            }
        }

        // Зачистка оперативной памяти после каждого цикла
        pngBytes = null;
        dibBytes = null;
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
    }

    private byte[]? GetClipboardBytes(uint format) {
        IntPtr hMem = GetClipboardData(format);
        if (hMem == IntPtr.Zero) return null;

        UIntPtr size = GlobalSize(hMem);
        if (size == UIntPtr.Zero) return null;

        byte[] buffer = new byte[(int)size];
        IntPtr pMem = GlobalLock(hMem);
        if (pMem == IntPtr.Zero) return null;

        Marshal.Copy(pMem, buffer, 0, buffer.Length);
        GlobalUnlock(hMem);
        return buffer;
    }

    private IntPtr CreateGlobalBuffer(byte[] data) {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return IntPtr.Zero;

        IntPtr pMem = GlobalLock(hMem);
        if (pMem == IntPtr.Zero) {
            GlobalFree(hMem);
            return IntPtr.Zero;
        }

        Marshal.Copy(data, 0, pMem, data.Length);
        GlobalUnlock(hMem);
        return hMem;
    }

    private IntPtr CreateHDropBuffer(string filePath) {
        int structSize = 20; 
        byte[] fileBytes = Encoding.Unicode.GetBytes(filePath + "\0\0"); 
        int totalSize = structSize + fileBytes.Length;

        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)totalSize);
        if (hMem == IntPtr.Zero) return IntPtr.Zero;

        IntPtr pMem = GlobalLock(hMem);
        if (pMem == IntPtr.Zero) {
            GlobalFree(hMem);
            return IntPtr.Zero;
        }

        Marshal.WriteInt32(pMem, 0, structSize); 
        Marshal.WriteInt32(pMem, 16, 1); 

        IntPtr pFiles = new IntPtr(pMem.ToInt64() + structSize);
        Marshal.Copy(fileBytes, 0, pFiles, fileBytes.Length);

        GlobalUnlock(hMem);
        return hMem;
    }

    private void CleanupOldFiles() {
        try {
            var files = Directory.EnumerateFiles(TargetDir, "scr_*.png");
            foreach (string file in files) {
                try {
                    if (File.GetCreationTime(file) < DateTime.Now.AddDays(-1)) {
                        File.Delete(file);
                    }
                } catch { }
            }
        } catch { }
    }

    public void Dispose() {
        RemoveClipboardFormatListener(this.Handle);
        this.DestroyHandle();
    }

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        using (var interceptor = new ClipboardInterceptor()) {
            Application.Run(); 
        }
    }
}