using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace AEther.WindowsForms
{
    public class GraphicsModule : IDisposable
    {

        const bool UseMapping = true;

        readonly Graphics Graphics;
        readonly SpectrumAccumulator<float>[] Spectrum;
        readonly Histogram[] Histogram;
        readonly ListBox States;
        readonly ConstantBuffer<FrameConstants> FrameConstants;

        int EventCounter = 0;
        readonly object AudioLock = new();
        readonly Stopwatch Clock = Stopwatch.StartNew();
        double PreviousTime;
        double PendingTransient;
        readonly ImpulseMotion Motion = new();
        readonly HistoryScroll HistoryScroll;
        readonly double AnalysisRate;
        readonly double[] SilentHistoryRow;
        readonly StormResponse Storm;
        readonly SpectralControlExtractor Separation;
        readonly RetainedVisualFeatures VisualFeatures;
        SpectralControls Separated;
        bool HasSeparation;
        AudioFeatures Features;
        double LastFeaturesTime;
        double SilenceRowsAccrued;
        double MusicalTime;

        public GraphicsModule(Graphics graphics, ListBox states, int channelCount, int noteCount, int historyCount, double analysisRate = 256, double historyPresentationDelay = 0, AnalyzerOptions? options = null)
        {
            Graphics = graphics;
            States = states;
            HistoryScroll = new HistoryScroll(analysisRate, historyPresentationDelay);
            AnalysisRate = analysisRate;
            SilentHistoryRow = new double[4 * noteCount];
            Storm = new StormResponse(options);
            Separation = new SpectralControlExtractor(options?.Domain ?? new Domain(27.5, 8, noteCount), channelCount);
            VisualFeatures = new RetainedVisualFeatures(options?.Domain ?? new Domain(27.5, 8, noteCount), channelCount, analysisRate);

            FrameConstants = Graphics.CreateConstantBuffer<FrameConstants>();
            Graphics.ShaderConstants["FrameConstants"] = FrameConstants.Buffer;

            var spectra = new List<SpectrumAccumulator<float>>();
            var histories = new List<Histogram>();
            states.Items.Clear();
            try
            {
                for (var c = 0; c < channelCount; ++c)
                {
                    spectra.Add(new FloatSpectrum(Graphics, noteCount));
                    histories.Add(new FloatHistogram(Graphics, noteCount, historyCount, UseMapping));
                }
                Spectrum = spectra.ToArray();
                Histogram = histories.ToArray();
                states.Items.Add(new MandelboxState(Graphics, Spectrum, Histogram));
                states.Items.Add(new StormState(Graphics, Storm));
                states.Items.Add(new SpectrumState(Graphics, Spectrum));
                states.Items.Add(new HistogramState(Graphics, Histogram));
                states.Items.Add(new HistoryTunnelState(Graphics, Histogram));
                states.Items.Add(new SceneState(Graphics, Spectrum));
                states.Items.Add(new FluidState(Graphics));
                states.Items.Add(new IFSState(Graphics));
                states.SelectedIndex = 0;
                Clock.Restart();
            }
            catch
            {
                foreach (var state in states.Items.OfType<GraphicsState>())
                    state.Dispose();
                states.Items.Clear();
                foreach (var spectrum in spectra)
                    spectrum.Dispose();
                foreach (var history in histories)
                    history.Dispose();
                FrameConstants.Dispose();
                Graphics.ShaderConstants.Remove("FrameConstants");
                throw;
            }

        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            foreach (var spectrum in Spectrum)
            {
                spectrum.Dispose();
            }
            foreach (var histogram in Histogram)
            {
                histogram.Dispose();
            }
            foreach (var state in States.Items.OfType<GraphicsState>())
            {
                state.Dispose();
            }
            FrameConstants.Dispose();
            Graphics.ShaderConstants.Remove("FrameConstants");
            States.Items.Clear();
        }

        public void ProcessKeyPress(KeyPressEventArgs evt)
        {
            if (States.SelectedItem is MandelboxState or HistoryTunnelState)
            {
                switch (char.ToLowerInvariant(evt.KeyChar))
                {
                    case 'm': Storm.CameraMovement = Storm.CameraMovement == 1 ? .35 : Storm.CameraMovement == .35 ? 0 : 1; break;
                    case '+': case '=': Storm.InputGain = Math.Min(12, Storm.InputGain * 1.2); break;
                    case '-': Storm.InputGain = Math.Max(.5, Storm.InputGain / 1.2); break;
                }
            }
            if (States.SelectedItem is GraphicsState state)
                state.ProcessKeyPress(evt);
        }

        public PipelineProfile Profile { get; set; } = new();

        public void Process(SampleEvent<double> evt)
        {
            var wait = Stopwatch.GetTimestamp();
            lock (AudioLock)
            {
                Profile.Elapsed("subscriber.graphics_lock_wait_ms", wait);
                var separated = Separation.Process(evt);
                Separated = new(separated.LowSustain, Math.Max(Separated.HighTransient, separated.HighTransient));
                HasSeparation = true;
                Features = VisualFeatures.Process(evt);
                MusicalTime = Features.MusicalTime;
                PendingTransient = Math.Max(PendingTransient, VisualFeatures.Transient);
                LastFeaturesTime = Clock.Elapsed.TotalSeconds;
                foreach (var (spectrum, c) in Spectrum.WithIndex())
                {
                    var channel = evt.GetChannel(c);
                    spectrum.Add(channel.Span);
                }
                foreach (var (histogram, c) in Histogram.WithIndex())
                {
                    var channel = evt.GetChannel(c);
                    histogram.Add(channel.Span);
                }
                HistoryScroll.AddRow();
                SilenceRowsAccrued = 0;
                Interlocked.Increment(ref EventCounter);
            }
        }

        public void ProcessTransient(double strength)
        {
            lock (AudioLock)
                PendingTransient = Math.Max(PendingTransient, strength);
        }

        public void Render()
        {
            using var frameTiming = Profile.Measure("frame.total_ms");

            var elapsed = Clock.Elapsed.TotalSeconds;
            var frameSeconds = Math.Max(0, elapsed - PreviousTime);
            var dt = (float)Math.Min(frameSeconds, .05);
            PreviousTime = elapsed;
            var t = (float)elapsed;
            Graphics.ElapsedSeconds = t;
            double transient;
            float histogramShift;
            float historySpan;
            AudioFeatures features;
            SpectralControls? separated;
            double musicalTime;

            // Upload before drawing, and never read/clear buffers while analysis writes them.
            var wait = Stopwatch.GetTimestamp();
            lock (AudioLock)
            {
                Profile.Elapsed("frame.audio_lock_wait_ms", wait);
                using var uploadTiming = Profile.Measure("frame.audio_state_upload_ms");
                transient = PendingTransient;
                PendingTransient = 0;
                // Loopback capture may send no packets when playback stops; still let the scene exhale.
                features = elapsed - LastFeaturesTime < .15 ? Features : default;
                musicalTime = MusicalTime;
                separated = HasSeparation ? (elapsed - LastFeaturesTime < .15 ? Separated : default(SpectralControls)) : null;
                Separated = Separated with { HighTransient = 0 };
                // Loopback can stop sending packets in silence. Write actual
                // zero rows so old light moves through the history and expires;
                // the next real batch then follows the same ring chronology.
                if (elapsed - LastFeaturesTime >= .15)
                {
                    SilenceRowsAccrued += frameSeconds * AnalysisRate;
                    var rows = (int)Math.Min(Histogram[0].Length, Math.Floor(SilenceRowsAccrued));
                    SilenceRowsAccrued = rows == Histogram[0].Length ? 0 : SilenceRowsAccrued - rows;
                    for (var row = 0; row < rows; ++row)
                    {
                        foreach (var histogram in Histogram)
                            histogram.Add(SilentHistoryRow);
                        HistoryScroll.AddRow();
                    }
                    if (rows > 0) Interlocked.Increment(ref EventCounter);
                }
                HistoryScroll.Advance(frameSeconds);
                histogramShift = (float)HistoryScroll.TextureCoordinate(Histogram[0].Length);
                historySpan = (float)HistoryScroll.AvailableSpan(Histogram[0].Length);
                if (0 < Interlocked.Exchange(ref EventCounter, 0))
                {
                    foreach (var spectrum in Spectrum)
                    {
                        spectrum.Update();
                        spectrum.Clear();
                    }
                    foreach (var histogram in Histogram)
                        histogram.Update();
                }
            }

            Motion.AddImpulse(transient);
            Motion.Advance(dt);
            Storm.Advance(frameSeconds, features, transient, separated);
            // Immediate illumination, followed by a short continuous decay through the atmosphere.
            Graphics.RhythmLight = Math.Max((float)transient, Graphics.RhythmLight * MathF.Exp(-dt / .18f));

            FrameConstants.Value = new()
            {
                AspectRatio = Graphics.BackBuffer.Width / (float)Graphics.BackBuffer.Height,
                T = t,
                DT = dt,
                HistogramShift = histogramShift,
                Transient = (float)transient,
                Motion = (float)Motion.Position,
                MotionVelocity = (float)Motion.Velocity,
                HistorySpan = historySpan,
                PoseX = (float)Storm.Pitch,
                PoseY = (float)Storm.Yaw,
                PoseZ = (float)Storm.Roll,
                Breath = (float)(Storm.Breath * Storm.MovementScale),
                VisualEnergy = (float)Storm.Energy,
                VisualFlash = (float)Storm.Flash,
                VisualGlow = (float)Storm.Afterglow,
                VisualSustain = (float)Storm.LowSustain,
                S = (float)musicalTime,
            };
            FrameConstants.Update();

            if (States.SelectedItem is GraphicsState state)
            {
                using (Profile.Measure("frame.draw_submit_ms")) state.Render();
                using (Profile.Measure("frame.present_wait_ms")) Graphics.Present();
            }
        }
    }
}
