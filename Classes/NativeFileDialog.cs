using System;
using System.Runtime.InteropServices;

namespace ExileMaps.Classes
{
    /// <summary>
    /// Minimal native file dialog via comdlg32 (GetOpenFileNameW / GetSaveFileNameW). Used instead of
    /// System.Windows.Forms dialogs because the host is a DirectX overlay (ClickableTransparentOverlay),
    /// not a WinForms app — WinForms ShowDialog faults the overlay. The native common dialog runs its own
    /// modal loop and needs no message pump. Call from an STA thread. Returns null on cancel/error.
    /// </summary>
    internal static class NativeFileDialog
    {
        // JSON, then all files. Win32 wants the pairs separated and terminated by NUL bytes.
        private const string Filter = "JSON files (*.json)\0*.json\0All files (*.*)\0*.*\0";

        public static string ShowOpen(string title, string initialDir) {
            var ofn = NewStruct(title, initialDir, null);
            ofn.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;
            return GetOpenFileNameW(ref ofn) ? ofn.lpstrFile : null;
        }

        public static string ShowSave(string title, string defaultFileName, string initialDir) {
            var ofn = NewStruct(title, initialDir, defaultFileName);
            ofn.Flags = OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR;
            ofn.lpstrDefExt = "json";
            return GetSaveFileNameW(ref ofn) ? ofn.lpstrFile : null;
        }

        private static OPENFILENAME NewStruct(string title, string initialDir, string fileName) {
            // Buffer must be pre-allocated and large enough for the returned path.
            var fileBuffer = new string('\0', 1024);
            if (!string.IsNullOrEmpty(fileName))
                fileBuffer = fileName + new string('\0', 1024 - fileName.Length);

            var ofn = new OPENFILENAME {
                lpstrFilter = Filter,
                lpstrFile = fileBuffer,
                nMaxFile = 1024,
                lpstrTitle = title,
                lpstrInitialDir = initialDir,
                hwndOwner = IntPtr.Zero,
            };
            ofn.lStructSize = Marshal.SizeOf(ofn);
            return ofn;
        }

        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);
    }
}
