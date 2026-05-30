/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// A scrolling spectrogram ("waterfall") of incoming audio. Feed it PCM (32k/16/mono)
/// via <see cref="PushPcm"/>; it FFTs each block, maps magnitude to colour, and scrolls
/// the history downward. Self-contained — the FFT is a small radix-2 Cooley-Tukey below.
/// </summary>
public sealed class WaterfallView : Control
{
    private const int Bins = 256;          // displayed magnitude points
    private const int Fft = Bins * 2;      // 512-point FFT
    private const int History = 220;       // rows of history

    private readonly WriteableBitmap bitmap =
        new(new PixelSize(Bins, History), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    private readonly float[] re = new float[Fft];
    private readonly float[] im = new float[Fft];
    private readonly int[] pixels = new int[Bins * History];   // BGRA8888, row-major; row 0 = oldest
    private readonly object sync = new object();

    public WaterfallView()
    {
        int dark = unchecked((int)0xFF101418);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = dark;
    }

    /// <summary>Feeds one PCM block (16-bit LE mono). Computes a spectrum row and scrolls.</summary>
    public void PushPcm(byte[] pcm, int count)
    {
        int samples = count / 2;
        if (samples < Fft) return;

        lock (sync)
        {
            int start = (samples - Fft) * 2;   // most recent window
            for (int i = 0; i < Fft; i++)
            {
                short s = (short)(pcm[start + i * 2] | (pcm[start + i * 2 + 1] << 8));
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1));   // Hann
                re[i] = (float)(s / 32768.0 * w);
                im[i] = 0;
            }
            FftForward(re, im);

            Array.Copy(pixels, Bins, pixels, 0, Bins * (History - 1));   // scroll up
            int rowOffset = Bins * (History - 1);
            for (int b = 0; b < Bins; b++)
            {
                double mag = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                double db = 20 * Math.Log10(mag + 1e-6);
                double norm = Math.Clamp((db + 90) / 90.0, 0, 1);
                pixels[rowOffset + b] = Heat(norm);
            }
        }

        Dispatcher.UIThread.Post(Blit);
    }

    private void Blit()
    {
        using (var fb = bitmap.Lock())
        {
            lock (sync)
            {
                for (int row = 0; row < History; row++)
                    Marshal.Copy(pixels, row * Bins, fb.Address + row * fb.RowBytes, Bins);
            }
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawImage(bitmap, new Rect(0, 0, Bins, History), new Rect(Bounds.Size));
    }

    // Blue→cyan→green→yellow→red heat ramp, packed BGRA8888 (opaque).
    private static int Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if (t < 0.25) { r = 0; g = 4 * t; b = 1; }
        else if (t < 0.5) { r = 0; g = 1; b = 1 - 4 * (t - 0.25); }
        else if (t < 0.75) { r = 4 * (t - 0.5); g = 1; b = 0; }
        else { r = 1; g = 1 - 4 * (t - 0.75); b = 0; }
        int R = (int)(r * 255), G = (int)(g * 255), B = (int)(b * 255);
        return unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
    }

    // In-place radix-2 Cooley-Tukey FFT (length must be a power of two).
    private static void FftForward(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float xr = re[b] * cr - im[b] * ci;
                    float xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}
