using System;
using System.IO;
using System.Threading;

namespace AEther
{
    public class WAVReader : SampleSource
    {

        public override SampleFormat Format { get; }

        public int BufferSize { get; set; } = 1 << 10;

        readonly Stream Input;
        readonly CancellationTokenSource Cancel;

        public WAVReader(Stream input)
        {
            Input = input;
            Format = WAVHeader.FromStream(input).GetSampleFormat();
            Cancel = new CancellationTokenSource();
        }

        public override void Start()
        {
            var buffer = new byte[BufferSize];
            while (!Cancel.IsCancellationRequested)
            {
                var count = Input.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }
                DataAvailable?.Invoke(this, buffer.AsSpan(0, count));
            }
            //OnStopped?.Invoke(this, null);
        }

        public override void Stop()
        {
            Cancel.Cancel();
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
        }

    }
}

