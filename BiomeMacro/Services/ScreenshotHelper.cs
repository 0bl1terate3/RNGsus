using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;

namespace BiomeMacro.Services
{
    public static class ScreenshotHelper
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static Bitmap? CaptureWindowBitmap(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero) return null;

                GetWindowRect(hWnd, out RECT rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0) return null;

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT = 2
                        PrintWindow(hWnd, hdc, 2);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static byte[]? CaptureWindow(IntPtr hWnd)
        {
            using (var bmp = CaptureWindowBitmap(hWnd))
            {
                if (bmp == null) return null;
                using (var stream = new System.IO.MemoryStream())
                {
                    // Use Jpeg or Bmp for speed if Png is too slow, but Png is default
                    bmp.Save(stream, ImageFormat.Png); 
                    return stream.ToArray();
                }
            }
        }
    }
}
