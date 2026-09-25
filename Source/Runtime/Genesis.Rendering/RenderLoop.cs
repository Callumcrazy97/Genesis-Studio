using System;
using System.Diagnostics;
using System.Threading;

namespace Genesis.Rendering
{
    // High-precision render loop driven by a dedicated Stopwatch thread.
    // Timing runs on a high-priority thread; actual D3D11 work is posted to the
    // UI thread via postToUiThread (typically form.BeginInvoke), keeping the
    // immediate context single-threaded and safe.
    //
    // Usage:
    //   var (thread, cts) = RenderLoop.Create(onTick, BeginInvoke, () => _fps, () => ContainsFocus);
    //   thread.Start();
    //   // on close:
    //   cts.Cancel(); thread.Join(500);
    public static class RenderLoop
    {
        // getTargetFps: called each frame to read the current cap (0 = uncapped).
        // isFocused:    called to detect focus loss; applies unfocusedFps cap.
        public static (Thread thread, CancellationTokenSource cts) Create(
            Action          onTick,
            Action<Action>  postToUiThread,
            Func<int>       getTargetFps,
            Func<bool>      isFocused,
            int             unfocusedFps = 20,
            string          name         = "RenderThread")
        {
            var cts          = new CancellationTokenSource();
            var token        = cts.Token;
            int tickPending  = 0;

            var thread = new Thread(() =>
            {
                var  sw            = Stopwatch.StartNew();
                long lastTick      = sw.ElapsedTicks;
                long spinThreshold = Stopwatch.Frequency / 500; // 2 ms

                while (!token.IsCancellationRequested)
                {
                    int fps = isFocused() ? getTargetFps() : unfocusedFps;
                    long interval = fps > 0
                        ? Stopwatch.Frequency / fps
                        : 0;  // 0 = uncapped (render every loop)

                    long now     = sw.ElapsedTicks;
                    long elapsed = now - lastTick;

                    if (interval == 0 || elapsed >= interval)
                    {
                        // Advance tick anchor; clamp spiral-of-death (> 4 frames behind).
                        if (interval > 0)
                        {
                            lastTick += interval;
                            if (lastTick < now - interval * 4)
                                lastTick = now;
                        }
                        else
                        {
                            lastTick = now;
                        }

                        // Only post if the previous frame has been processed.
                        if (Interlocked.CompareExchange(ref tickPending, 1, 0) == 0)
                        {
                            try
                            {
                                postToUiThread(() =>
                                {
                                    Interlocked.Exchange(ref tickPending, 0);
                                    onTick();
                                });
                            }
                            catch
                            {
                                Interlocked.Exchange(ref tickPending, 0);
                            }
                        }
                    }
                    else
                    {
                        long remaining = interval - elapsed;
                        if (remaining > spinThreshold)
                            Thread.Sleep(1);
                        // else spin-wait the final 2 ms for precise timing
                    }
                }
            })
            {
                IsBackground = true,
                Priority     = ThreadPriority.AboveNormal,
                Name         = name
            };

            return (thread, cts);
        }
    }
}
