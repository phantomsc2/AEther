using System;
using System.IO;

namespace AEther
{
    public record WAVHeader
    (
        RIFFHeader RIFF,
        FormatHeader Format,
        DataHeader Data
    )
    {

        public static WAVHeader FromStream(Stream stream)
        {
            var reader = new BinaryReader(stream);
            var riff = RIFFHeader.FromStream(reader);
            var format = FormatHeader.FromStream(reader);
            var data = DataHeader.FromStream(reader);
            return new WAVHeader(riff, format, data);
        }

        public SampleType GetSampleType()
            => Format.Tag switch
            {
                WAVFormatTag.PCM when Format.BitsPerSample == 16 => SampleType.Int16.Instance,
                WAVFormatTag.IEEEFloat when Format.BitsPerSample == 32 => SampleType.Float32.Instance,
                _ => throw new FormatException(Format.Tag.ToString()),
            };

        public SampleFormat GetSampleFormat()
            => new(GetSampleType(), (int)Format.SamplesPerSecond, Format.ChannelCount);

    }
}
