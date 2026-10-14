using System;
using System.Diagnostics;
using System.Windows.Forms;
using System.Threading;

namespace AEther.WindowsForms
{
    public class StatisticsModule
    {

        readonly TimeSpan UpdateInterval;
        readonly Form Form;
        readonly Stopwatch FrameTimer;
        readonly Stopwatch UpdateTimer;

        long LatencyTicks;

        public StatisticsModule(Form form, double interval = 1)
        {
            UpdateInterval = TimeSpan.FromSeconds(interval);
            Form = form;
            FrameTimer = Stopwatch.StartNew();
            UpdateTimer = Stopwatch.StartNew();
        }

        public void Update(long droppedInputCount = 0)
        {

            var frameTime = FrameTimer.Elapsed;
            FrameTimer.Restart();

            if (UpdateInterval < UpdateTimer.Elapsed)
            {
                var latency = TimeSpan.FromTicks(Interlocked.Exchange(ref LatencyTicks, 0));
                Form.Text = $"Analysis age max: {Math.Round(latency.TotalMilliseconds, 0)} ms, Frame interval: {Math.Round(frameTime.TotalMilliseconds, 0)} ms, Dropped input: {droppedInputCount}";
                UpdateTimer.Restart();
            }

        }

        public void Process(SampleEvent<double> evt)
        {
            var latency = Math.Max(0, (DateTime.Now - evt.Time).Ticks);
            long previous;
            do
            {
                previous = Interlocked.Read(ref LatencyTicks);
                if (previous >= latency)
                    return;
            } while (Interlocked.CompareExchange(ref LatencyTicks, latency, previous) != previous);
        }

    }
}
