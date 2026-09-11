using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeckLinkStreamStudio.Engines;

public sealed class StreamPreviewReader : IDisposable
{
    private readonly Stream _inputStream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bytesPerFrame;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private bool _disposed;

    public event Action<Bitmap>? OnFrameAvailable;
    public event Action<Exception>? OnError;

    public StreamPreviewReader(Stream inputStream, int width, int height)
    {
        _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
        _width = width;
        _height = height;
        _bytesPerFrame = width * height * 3; // 24-bit BGR (3 bytes per pixel)
    }

    public void Start()
    {
        if (_readTask != null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _readTask = Task.Run(() => ReadLoop(token), token);
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[_bytesPerFrame];

        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                int totalRead = 0;
                while (totalRead < _bytesPerFrame && !token.IsCancellationRequested && !_disposed)
                {
                    int bytesRead = _inputStream.Read(buffer, totalRead, _bytesPerFrame - totalRead);
                    if (bytesRead <= 0)
                    {
                        // Pipe closed
                        return;
                    }
                    totalRead += bytesRead;
                }

                if (token.IsCancellationRequested || _disposed) break;

                // Create bitmap from buffer
                var bitmap = new Bitmap(_width, _height, PixelFormat.Format24bppRgb);
                var bmpData = bitmap.LockBits(
                    new Rectangle(0, 0, _width, _height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb
                );

                try
                {
                    Marshal.Copy(buffer, 0, bmpData.Scan0, _bytesPerFrame);
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }

                OnFrameAvailable?.Invoke(bitmap);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                OnError?.Invoke(ex);
            }
        }
    }

    public void Stop()
    {
        _disposed = true;
        _cts?.Cancel();
        try
        {
            _inputStream.Close();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
