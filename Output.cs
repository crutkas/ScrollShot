using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScrollShot;

static partial class Program
{
    internal static unsafe class Output
    {
        // PNG via Windows Imaging Component (built into Windows; no extra dependencies).
        public static void SavePng(string path, Image img)
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            IWICImagingFactory* factory = null;
            IWICStream* stream = null;
            IWICBitmapEncoder* encoder = null;
            IWICBitmapFrameEncode* frame = null;
            try
            {
                Guid clsid = PInvoke.CLSID_WICImagingFactory;
                Guid iid = IWICImagingFactory.IID_Guid;
                PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).ThrowOnFailure();
                factory->CreateStream(&stream);
                stream->InitializeFromFilename(path, 0x40000000 /*GENERIC_WRITE*/);
                Guid png = PInvoke.GUID_ContainerFormatPng;
                encoder = factory->CreateEncoder(&png, null);
                encoder->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
                IPropertyBag2* bag = null;
                encoder->CreateNewFrame(&frame, &bag);
                frame->Initialize(bag);
                if (bag != null) bag->Release();
                frame->SetSize((uint)img.W, (uint)img.H);
                Guid fmt = PInvoke.GUID_WICPixelFormat32bppBGRA;
                frame->SetPixelFormat(&fmt);
                // Write in bands to keep each call's buffer size reasonable for tall images.
                fixed (int* px = img.Px)
                {
                    const int band = 2048;
                    for (int y = 0; y < img.H; y += band)
                    {
                        int rows = Math.Min(band, img.H - y);
                        frame->WritePixels((uint)rows, (uint)(img.W * 4), (uint)(rows * img.W * 4), (byte*)(px + (long)y * img.W));
                    }
                }
                frame->Commit();
                encoder->Commit();
            }
            finally
            {
                if (frame != null) frame->Release();
                if (encoder != null) encoder->Release();
                if (stream != null) stream->Release();
                if (factory != null) factory->Release();
            }
        }

        // Puts the image on the clipboard as both a bitmap (CF_DIB) and PNG (for apps that prefer it).
        public static void CopyToClipboard(Image img, string pngPath)
        {
            bool open = false;
            for (int i = 0; i < 10 && !(open = PInvoke.OpenClipboard(HWND.Null)); i++) Thread.Sleep(50);
            if (!open) throw new InvalidOperationException("clipboard is busy");
            try
            {
                PInvoke.EmptyClipboard();

                var png = File.ReadAllBytes(pngPath);
                SetData(PInvoke.RegisterClipboardFormat("PNG"), png.Length, dst => png.AsSpan().CopyTo(dst));

                long size = sizeof(BITMAPINFOHEADER) + (long)img.W * img.H * 4;
                if (size < int.MaxValue)
                {
                    SetData(8 /*CF_DIB*/, (int)size, dst =>
                    {
                        var h = new BITMAPINFOHEADER
                        {
                            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = img.W, biHeight = img.H, // bottom-up
                            biPlanes = 1, biBitCount = 32, biSizeImage = (uint)(img.W * img.H * 4),
                        };
                        MemoryMarshalWrite(dst, h);
                        var pixels = dst[sizeof(BITMAPINFOHEADER)..];
                        var src = img.Px.AsSpan();
                        for (int y = 0; y < img.H; y++)
                        {
                            var row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(src.Slice((img.H - 1 - y) * img.W, img.W));
                            row.CopyTo(pixels.Slice(y * img.W * 4));
                        }
                    });
                }
            }
            finally { PInvoke.CloseClipboard(); }
        }

        static void MemoryMarshalWrite(Span<byte> dst, BITMAPINFOHEADER h) =>
            System.Runtime.InteropServices.MemoryMarshal.Write(dst, in h);

        delegate void Fill(Span<byte> dst);

        static void SetData(uint format, int size, Fill fill)
        {
            var mem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)size);
            if (mem == default) throw new OutOfMemoryException();
            var p = PInvoke.GlobalLock(mem);
            fill(new Span<byte>(p, size));
            PInvoke.GlobalUnlock(mem);
            if (PInvoke.SetClipboardData(format, new HANDLE((nint)mem.Value)) == default) PInvoke.GlobalFree(mem);
        }

        public static void Open(string path) =>
            PInvoke.ShellExecute(HWND.Null, "open", path, null, null, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
    }
}
