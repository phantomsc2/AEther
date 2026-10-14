using AEther.DMX;
using AEther.NAudio;
using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace AEther.WindowsForms
{
    public partial class MainForm : Form
    {

        public const string LoopbackEntry = "Loopback";

        public readonly Graphics Graphics;
        Session? Session;
        readonly SemaphoreSlim SessionGate = new(1, 1);
        readonly System.Windows.Forms.Timer ReloadTimer = new() { Interval = 250 };
        bool IsShuttingDown;
        bool ShutdownComplete;
        bool Ready;

        public MainForm()
        {
            InitializeComponent();
            Graphics = new Graphics(Handle);
            Graphics.OnShaderChange += Graphics_OnShaderChange;
            ReloadTimer.Tick += ReloadTimer_Tick;
            Input.Items.Clear();
            Input.Items.Add(LoopbackEntry);
            Input.Items.AddRange(Recorder.GetAvailableDeviceNames().ToArray());
            Input.SelectedIndex = 0;
            AnalyzerOptions.SelectedObject = new AnalyzerOptions();
            DMXOptions.SelectedObject = new DMXOptions();
            Ready = true;
        }

        private void Graphics_OnShaderChange(object? sender, string e)
        {
            if (IsShuttingDown || IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsShuttingDown)
                        return;
                    ReloadTimer.Stop();
                    ReloadTimer.Start();
                }));
            }
            catch (InvalidOperationException) when (IsShuttingDown || IsDisposed) { }
        }

        private async void ReloadTimer_Tick(object? sender, EventArgs e)
        {
            ReloadTimer.Stop();
            await ResetAsync(validateShaders: true);
        }

        async Task StartAsync()
        {
            if (AnalyzerOptions.SelectedObject is not AnalyzerOptions options)
            {
                throw new InvalidCastException();
            }
            if (DMXOptions.SelectedObject is not DMXOptions dmxOptions)
            {
                throw new InvalidCastException();
            }
            SampleSource source = Input.SelectedItem?.ToString() switch
            {
                LoopbackEntry => new Loopback(),
                string s => new Recorder(s),
                _ => throw new Exception(),
            };
            Session? session = null;
            try
            {
                session = new(this, source, States, options, dmxOptions);
                session.Start();
                Session = session;
            }
            catch
            {
                if (session is not null)
                {
                    try { await session.StopAsync(); }
                    finally { session.Dispose(); }
                }
                else
                    source.Dispose();
                throw;
            }

        }

        public async Task StopAsync()
        {
            await SessionGate.WaitAsync();
            try
            {
                await StopCoreAsync();
            }
            finally { SessionGate.Release(); }
        }

        async Task StopCoreAsync()
        {
            var session = Session;
            Session = null;
            if (session is null)
                return;
            try { await session.StopAsync(); }
            finally { session.Dispose(); }
        }

        public void Render()
        {
            if (Session is Session session)
            {
                session.Render();
            }
        }

        void ToggleFullscreen()
        {
            if (Graphics.IsFullscreen)
            {
                Graphics.IsFullscreen = false;
                Graphics.SetMode(Graphics.NativeMode with
                {
                    Width = ClientSize.Width,
                    Height = ClientSize.Height,
                });
            }
            else
            {
                Graphics.IsFullscreen = true;
                Graphics.SetMode(Graphics.NativeMode);
            }
        }

        async Task ResetAsync(bool validateShaders = false)
        {
            if (!Ready || !Created || IsShuttingDown)
            {
                return;
            }
            await SessionGate.WaitAsync();
            try
            {
                if (IsShuttingDown)
                    return;
                if (validateShaders)
                    Graphics.ValidateShaders();
                var selected = States.SelectedIndex;
                await StopCoreAsync();
                if (!IsShuttingDown)
                {
                    await StartAsync();
                    if (selected >= 0 && selected < States.Items.Count)
                        States.SelectedIndex = selected;
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "AEther could not restart", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { SessionGate.Release(); }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            Graphics.SetMode(Graphics.NativeMode with
            {
                Width = ClientSize.Width,
                Height = ClientSize.Height,
            });
        }

        protected async override void OnFormClosing(FormClosingEventArgs e)
        {
            if (ShutdownComplete)
            {
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            base.OnFormClosing(e);
            if (IsShuttingDown)
                return;
            IsShuttingDown = true;
            ReloadTimer.Stop();
            Graphics.OnShaderChange -= Graphics_OnShaderChange;
            try { await StopAsync(); }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
            finally
            {
                Graphics.Dispose();
                ReloadTimer.Dispose();
                ShutdownComplete = true;
                Close();
            }
        }

        protected async override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Graphics.SetMode(Graphics.NativeMode with
            {
                Width = ClientSize.Width,
                Height = ClientSize.Height,
            });
            await ResetAsync();
        }

        protected async override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Space:
                    Panel.Visible ^= true;
                    break;
                case Keys.F5:
                    await ResetAsync();
                    break;
                case Keys.F9:
                    if (Session is Session profilingSession)
                    {
                        var profile = profilingSession.Analyzer.Profile;
                        if (!profile.Enabled)
                            profile.Start();
                        else
                        {
                            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles");
                            var path = await Task.Run(() => profile.Save(folder));
                            MessageBox.Show(this, path, "Profile saved (CSV and summary)");
                        }
                    }
                    break;
                case Keys.F11:
                    ToggleFullscreen();
                    break;
                case Keys.Escape:
                    Close();
                    break;
            }
        }

        private async void Options_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
        {
            await ResetAsync();
        }

        private async void Input_SelectedValueChanged(object? sender, EventArgs e)
        {
            await ResetAsync();
        }

        private async void DMXOptions_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
        {
            await ResetAsync();
        }

    }
}
