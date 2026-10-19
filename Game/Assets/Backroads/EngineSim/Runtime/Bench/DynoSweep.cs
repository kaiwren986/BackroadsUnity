using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Backroads.EngineSim
{
    /// <summary>
    /// The Engine Bench's "Dyno sweep" (PLAN 6.1 M1): a second, silent instance of the selected engine on a background
    /// thread runs esp_dyno_sweep one point at a time (WOT, hold, settle, 1 s average), so the plot fills in while the
    /// live engine keeps running. The thread owns its handle (one control thread per handle, ADR-002).
    /// </summary>
    public sealed unsafe class DynoSweep
    {
        public struct Point
        {
            public double rpm, torqueNm, powerW;
        }

        readonly ConcurrentQueue<Point> _points = new ConcurrentQueue<Point>();
        Thread _thread;
        volatile bool _cancel;
        public volatile bool Running;
        public volatile string Error;
        public EngineDef Def { get; private set; }
        public int Total { get; private set; }

        public bool TryDequeue(out Point p) => _points.TryDequeue(out p);

        public void Start(EngineDef def, double fromRpm, double toRpm, double stepRpm, double settleS = 1.5)
        {
            Cancel();
            Def = def;
            Error = null;
            _cancel = false;
            Running = true;
            Total = (int)Math.Floor((toRpm - fromRpm) / stepRpm) + 1;
            // The engine is created on this (main) thread, which knows the paths; the sweep thread then owns it.
            var e = EngineSimEngine.Create(def, new EngineSimEngine.Options { audioSampleRate = 0, workerThread = true }, out var err);
            if (e == null)
            {
                Error = err;
                Running = false;
                return;
            }
            _thread = new Thread(() =>
            {
                try
                {
                    for (double rpm = fromRpm; rpm <= toRpm + 1e-6 && !_cancel; rpm += stepRpm)
                    {
                        double t = 0, p = 0;
                        int r = Native.esp_dyno_sweep(e.Handle, rpm, rpm, 1, 1.0f, settleS, &t, &p);
                        if (r != EspStatus.Ok) { Error = "esp_dyno_sweep " + EspStatus.Name(r); break; }
                        _points.Enqueue(new Point { rpm = rpm, torqueNm = t, powerW = p });
                    }
                }
                catch (Exception ex) { Error = ex.Message; }
                finally
                {
                    e.Dispose();
                    Running = false;
                }
            }) { IsBackground = true, Name = "EngineBench dyno sweep" };
            _thread.Start();
        }

        public void Cancel()
        {
            _cancel = true;
            _thread?.Join(10000);
            _thread = null;
            while (_points.TryDequeue(out _)) { }
        }
    }
}
