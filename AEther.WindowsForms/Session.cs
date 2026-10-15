using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AEther.WindowsForms
{
    public class Session : IDisposable
    {

        public readonly MainForm Form;
        public readonly StatisticsModule Statistics;
        public readonly SampleSource Source;
        public readonly Analyzer Analyzer;
        public readonly GraphicsModule GraphicsModule;
        Task? StopTask;
        bool IsDisposed;

        public Session(MainForm form, SampleSource source, ListBox states, AnalyzerOptions options)
        {
            Form = form;
            Statistics = new StatisticsModule(form);
            Source = source;
            Analyzer = new(source.Format, options);
            Analyzer.Profile.Describe("Source", source.GetType().FullName ?? source.GetType().Name);

            try
            {
                var analysisRate = source.Format.SampleRate / (double)(source.Format.SampleRate / options.TimeResolution);
                var historyCount = (int)Math.Clamp(Math.Ceiling(analysisRate * options.HistorySeconds), 2, 16384);
                GraphicsModule = new GraphicsModule(form.Graphics, states, Source.Format.ChannelCount, options.Domain.Length, historyCount, analysisRate, options.HistoryPresentationDelayMilliseconds / 1000, options);
            }
            catch
            {
                Source.Dispose();
                Analyzer.Stop();
                throw;
            }
            GraphicsModule.Profile = Analyzer.Profile;
            Analyzer.SamplesAnalyzed += Analyzer_SamplesAnalyzed;
            Source.DataAvailable += Source_DataAvailable;
            Form.KeyPress += Form_KeyPress;

        }

        private void Form_KeyPress(object? sender, KeyPressEventArgs e)
        {
            GraphicsModule.ProcessKeyPress(e);
        }

        private void Source_DataAvailable(object? sender, ReadOnlyMemory<byte> evt)
        {
            Analyzer.PostSamples(evt);
        }

        private void Analyzer_SamplesAnalyzed(object? sender, SampleEvent<double> evt)
        {
            Statistics.Process(evt);
            using (Analyzer.Profile.Measure("subscriber.graphics_ms")) GraphicsModule.Process(evt);
        }

        public void Start()
        {
            Analyzer.Start();
            Source.Start();
        }

        public void Render()
        {
            Statistics.Update(Analyzer.DroppedInputCount);
            GraphicsModule.Render();
        }

        public Task StopAsync() => StopTask ??= StopCoreAsync();

        async Task StopCoreAsync()
        {

            Analyzer.SamplesAnalyzed -= Analyzer_SamplesAnalyzed;
            Source.DataAvailable -= Source_DataAvailable;
            Form.KeyPress -= Form_KeyPress;
            try
            {
                Source.Stop();
            }
            finally
            {
                Analyzer.Stop();
                await Analyzer.Completion;
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            GC.SuppressFinalize(this);
            Source.Dispose();
            GraphicsModule.Dispose();
        }

    }
}
