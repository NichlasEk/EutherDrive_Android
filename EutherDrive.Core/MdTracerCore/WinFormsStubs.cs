using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    // MDTracer-kod råkar använda WinForms MessageBox.
    // I headless/Avalonia-läge gör vi den till en no-op + Debug-logg.
    internal enum DialogResult { OK, Cancel, Yes, No }
    internal enum MessageBoxButtons { OK, OKCancel, YesNo, YesNoCancel }
    internal enum MessageBoxIcon { None, Information, Warning, Error, Question }

    internal static class MessageBox
    {
        public static DialogResult Show(string text)
        {
            Debug.WriteLine(text);
            return DialogResult.OK;
        }

        public static DialogResult Show(string text, string caption)
        {
            Debug.WriteLine($"{caption}: {text}");
            return DialogResult.OK;
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Debug.WriteLine($"{caption} [{buttons}/{icon}]: {text}");
            return DialogResult.OK;
        }
    }
}
