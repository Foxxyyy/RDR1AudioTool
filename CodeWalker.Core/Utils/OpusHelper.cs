using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodeWalker.Core.Utils
{
    public class AwcOpusBlock
    {
        public uint StartEntry;      //From first 0x10 table
        public uint Entries;
        public uint ChannelSkip;
        public uint ChannelSamples;

        public uint ChannelSize;     //Computed
        public uint FrameSize;       //From "D11A" header
        public uint ChunkStart;      //Absolute offset to audio payload
        public uint ChunkSize;       //Computed

        public void Read(BinaryReader reader)
        {
            StartEntry = reader.ReadUInt32();
            Entries = reader.ReadUInt32();
            ChannelSkip = reader.ReadUInt32();
            ChannelSamples = reader.ReadUInt32();
        }

        public void ReadD11A(BinaryReader reader)
        {
            var tag = reader.ReadUInt32();
            if (tag != 0x41313144) //"D11A"
            {
                throw new Exception($"Unexpected OPUS tag: 0x{tag:X}");
            }

            FrameSize = reader.ReadUInt16(); //80 bytes per Opus packet
            reader.BaseStream.Position += 0x6A; //Padding (0x77 fill for OPUS/ATRAC)

            ChunkSize = Entries * FrameSize;
            ChannelSize = ChunkSize;
        }

        public void SetChunkStart(long offset)
        {
            ChunkStart = (uint)offset;
        }
    }

    public class OpusHelper
    {
        public static byte[] Decode(byte[] data, int channels, uint blockCount, int sampleRate)
        {
            if (data == null) return null;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            using var pcmStream = new MemoryStream();

            //Parse blocks
            var blocks = new AwcOpusBlock[channels];
            for (int ch = 0; ch < channels; ch++)
            {
                blocks[ch] = new AwcOpusBlock();
                blocks[ch].Read(br);
            }

            //Now read each header tables
            for (int ch = 0; ch < channels; ch++)
            {
                blocks[ch].ReadD11A(br);
            }

            //Now each channel has ChunkStart
            for (int ch = 0; ch < channels; ch++)
            {
                blocks[ch].SetChunkStart(br.BaseStream.Position);
                ms.Position += blocks[ch].ChunkSize;
            }

            ms.Position = (channels * 0x10) + (channels * 0x70);

            //Create one decoder per channel
            var decoders = new OpusDecoder[channels];
            for (int ch = 0; ch < channels; ch++)
            {
                decoders[ch] = OpusDecoder.Create(sampleRate, 1);
            }

            for (int blockId = 0; blockId < blockCount; blockId++)
            {
                var channelBuffers = new List<short[]>[channels];
                for (int ch = 0; ch < channels; ch++) //Decode frames for each channel
                {
                    var block = blocks[ch];
                    if (blockId > 0 && ch == 0) //Seek to next block on first channel only (others are relative to it)
                    {
                        ms.Position += (channels * 0x10) + (channels * 0x70);
                    }

                    var blockData = br.ReadBytes((int)blocks[ch].ChunkSize);
                    var pos = 0;
                    var frames = new List<short[]>();

                    while (pos < blockData.Length)
                    {
                        var size = (int)blocks[ch].FrameSize;
                        if (pos + size > blockData.Length) size = blockData.Length - pos;

                        var packet = new byte[size];
                        Array.Copy(blockData, pos, packet, 0, size);
                        pos += size;

                        var pcmBytes = decoders[ch].Decode(packet, out int decodedBytes);
                        var shorts = new short[decodedBytes / 2];
                        Buffer.BlockCopy(pcmBytes, 0, shorts, 0, decodedBytes);
                        frames.Add(shorts);
                    }
                    channelBuffers[ch] = frames;
                }

                //Interleave samples
                foreach (var frameId in Enumerable.Range(0, channelBuffers[0].Count))
                {
                    var samplesInFrame = channelBuffers[0][frameId].Length;
                    for (int s = 0; s < samplesInFrame; s++)
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            short sample = 0;
                            if (frameId < channelBuffers[ch].Count && s < channelBuffers[ch][frameId].Length)
                            {
                                sample = channelBuffers[ch][frameId][s];
                            }

                            var bytes = BitConverter.GetBytes(sample);
                            pcmStream.Write(bytes, 0, 2);
                        }
                    }
                }

                //Skip padding
                while (ms.Position < ms.Length)
                {
                    if (br.ReadByte() != 0x97)
                    {
                        ms.Position--;
                        break;
                    }
                }
            }
            return pcmStream.ToArray();
        }

        public static byte[] Encode(byte[] data, uint sampleCount, uint sampleRate)
        {
            //RDR1 OPUS expects 3 mono streams (dry, revL, revR) stored as 3 channel blocks
            const int outChannels = 3;
            const int frameSamples = 960; //20ms @ 48k
            const int frameBytes = 80; //Fixed packet size in the container

            var inShorts = new short[data.Length / 2];
            Buffer.BlockCopy(data, 0, inShorts, 0, data.Length);

            //Total frames = sampleCount, total shorts = frames * inChannels
            var inChannels = Math.Max(1, (int)(inShorts.Length / Math.Max(1, (int)sampleCount)));
            if (inChannels > 3)
                inChannels = 3;

            //Deinterleave input into per-channel arrays
            var frames = inShorts.Length / inChannels;
            var chIn = new short[inChannels][];

            for (int c = 0; c < inChannels; c++)
                chIn[c] = new short[frames];

            for (int i = 0; i < frames; i++)
                for (int c = 0; c < inChannels; c++)
                    chIn[c][i] = inShorts[i * inChannels + c];

            //Build 3 channels output
            //If mono: duplicate to all 3
            //If stereo: dry = (L+R)/2, revL=L, revR=R
            //If 3ch: passthrough
            var chOut = new short[outChannels][];
            for (int c = 0; c < outChannels; c++)
                chOut[c] = new short[frames];

            if (inChannels == 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    short m = chIn[0][i];
                    chOut[0][i] = m;
                    chOut[1][i] = m;
                    chOut[2][i] = m;
                }
            }
            else if (inChannels == 2)
            {
                for (int i = 0; i < frames; i++)
                {
                    int L = chIn[0][i];
                    int R = chIn[1][i];
                    short dry = (short)((L + R) / 2);

                    chOut[0][i] = dry; //dry
                    chOut[1][i] = (short)L; //revL
                    chOut[2][i] = (short)R; //revR
                }
            }
            else
            {
                Array.Copy(chIn[0], chOut[0], frames);
                Array.Copy(chIn[1], chOut[1], frames);
                Array.Copy(chIn[2], chOut[2], frames);
            }

            //Encode each output channel as its own MONO opus stream
            var packets = new List<byte[]>[outChannels];
            for (int c = 0; c < outChannels; c++)
                packets[c] = new List<byte[]>();

            using var enc0 = OpusEncoder.Create((int)sampleRate, 1, 2048);
            using var enc1 = OpusEncoder.Create((int)sampleRate, 1, 2048);
            using var enc2 = OpusEncoder.Create((int)sampleRate, 1, 2048);

            ConfigureEncoder(enc0);
            ConfigureEncoder(enc1);
            ConfigureEncoder(enc2);

            var encs = new[] { enc0, enc1, enc2 };
            var frameCount = (frames + frameSamples - 1) / frameSamples;

            for (int f = 0; f < frameCount; f++)
            {
                var start = f * frameSamples;
                var copy = Math.Min(frameSamples, frames - start);

                for (int c = 0; c < outChannels; c++)
                {
                    //Always feed exactly 960 samples (pad last frame with zeros)
                    var frame = new short[frameSamples];
                    if (copy > 0)
                        Array.Copy(chOut[c], start, frame, 0, copy);

                    var packet = encs[c].Encode(frame, frameSamples);

                    //Enforce fixed 80 bytes
                    if (packet.Length > frameBytes)
                    {
                        Array.Resize(ref packet, frameBytes);
                    }

                    var packet80 = new byte[frameBytes];
                    Buffer.BlockCopy(packet, 0, packet80, 0, packet.Length);

                    var ret = API.opus_packet_pad(packet80, packet.Length, frameBytes);
                    if (ret != 0)
                        throw new Exception($"opus_packet_pad failed: {ret}");

                    packets[c].Add(packet80);
                }
            }

            //[3 * 0x10] [3 * 0x70] [payload ch0][payload ch1][payload ch2]
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            //0x10 table per channel
            var startEntry = 0u;
            for (int c = 0; c < outChannels; c++)
            {
                var entries = (uint)packets[c].Count;
                bw.Write(startEntry); //start_entry
                bw.Write((uint)packets[c].Count); //entries
                bw.Write(0u); //channel_skip
                bw.Write((uint)(packets[c].Count * frameSamples)); //channel_samples
                startEntry += entries;
            }

            //0x70 per channel + pad 
            for (int c = 0; c < outChannels; c++)
            {
                var headerStart = ms.Position;
                bw.Write(0x41313144); //"D11A"
                bw.Write((ushort)frameBytes);
                bw.Write((ushort)frameSamples);
                bw.Write((ushort)0x0101);
                bw.Write((ushort)sampleRate);
                bw.Write(0x45313144); //"D11E"

                //Pad to exactly 0x70 bytes from headerStart
                while (ms.Position < headerStart + 0x70)
                    bw.Write((byte)0x77);
            }

            //Payloads
            for (int c = 0; c < outChannels; c++)
            {
                foreach (var p in packets[c])
                    bw.Write(p);
            }

            data = ms.ToArray();
            
            const int targetSize = 0x40000; //262144 seems to match
            if (data.Length < targetSize)
            {
                Array.Resize(ref data, targetSize);
                for (int i = (int)ms.Length; i < targetSize; i++)
                    data[i] = 0x97;
            }
            return data;
        }

        private static void ConfigureEncoder(OpusEncoder enc)
        {
            enc.SetSignalVoice(); //Speech tuning
            enc.SetVbr(false); //CBR for more stable packet size
            enc.SetVbrConstraint(true);
            enc.SetComplexity(3); //Reduces size spikes
            enc.SetBitrate(16000);
        }
    }

    public class OpusDecoder : IDisposable
    {
        private IntPtr _decoder;
        public int OutputSamplingRate { get; private set; }
        public int OutputChannels { get; private set; }
        public int MaxDataBytes { get; set; }
        public bool ForwardErrorCorrection { get; set; }

        private OpusDecoder(IntPtr decoder, int outputSamplingRate, int outputChannels)
        {
            _decoder = decoder;
            OutputSamplingRate = outputSamplingRate;
            OutputChannels = outputChannels;
            MaxDataBytes = 16384;
        }

        public static OpusDecoder Create(int outputSampleRate, int outputChannels)
        {
            IntPtr decoder = API.opus_decoder_create(outputSampleRate, outputChannels, out IntPtr error);
            if ((Errors)error != Errors.OK)
            {
                throw new Exception("Exception occured while creating decoder");
            }
            return new OpusDecoder(decoder, outputSampleRate, outputChannels);
        }

        public unsafe byte[] Decode(byte[] data, out int decodedLength)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("OpusDecoder");
            }

            IntPtr decodedPtr;
            byte[] decoded = new byte[MaxDataBytes];
            int length = 0;

            fixed (byte* bdec = decoded)
            {
                decodedPtr = new IntPtr(bdec);
                length = API.opus_decode(_decoder, data, data.Length, decodedPtr, 960, 0);
            }

            if (length < 0)
                decodedLength = 0;
            else
                decodedLength = length * 2 * OutputChannels;
            return decoded;
        }

        ~OpusDecoder()
        {
            Dispose();
        }

        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            GC.SuppressFinalize(this);

            if (_decoder != IntPtr.Zero)
            {
                API.opus_decoder_destroy(_decoder);
                _decoder = IntPtr.Zero;
            }
            disposed = true;
        }
    }

    public class OpusEncoder : IDisposable
    {
        private IntPtr _encoder;
        public int InputSamplingRate { get; private set; }
        public int InputChannels { get; private set; }
        public int MaxPacketSize { get; set; }

        private bool disposed;

        private OpusEncoder(IntPtr encoder, int sampleRate, int channels)
        {
            _encoder = encoder;
            InputSamplingRate = sampleRate;
            InputChannels = channels;
            MaxPacketSize = 4000; //Max bytes per Opus packet
        }

        public static OpusEncoder Create(int sampleRate, int channels, int application = 2049)
        {
            var enc = API.opus_encoder_create(sampleRate, channels, application, out IntPtr error);
            if ((Errors)error != Errors.OK)
            {
                throw new Exception($"Opus encoder creation failed: {(Errors)error}");
            }
            return new OpusEncoder(enc, sampleRate, channels);
        }

        public unsafe byte[] Encode(short[] pcm, int frameSize)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(OpusEncoder));
            }

            var encoded = new byte[MaxPacketSize];
            var length = 0;

            fixed (byte* pEnc = encoded)
            {
                length = API.opus_encode(_encoder, pcm, frameSize, (IntPtr)pEnc, MaxPacketSize);
            }

            if (length < 0)
            {
                throw new Exception($"Opus encode error: {(Errors)length}");
            }

            //Shrink to actual length
            var packet = new byte[length];
            Array.Copy(encoded, packet, length);
            return packet;
        }

        public void SetBitrate(int bps)
        {
            var ret = API.opus_encoder_ctl(_encoder, Ctl.SetBitrateRequest, bps);
            if (ret < 0)
            {
                throw new Exception($"Failed to set bitrate: {(Errors)ret}");
            }
        }

        public void SetVbr(bool enabled) => API.opus_encoder_ctl(_encoder, Ctl.SetVbrRequest, enabled ? 1 : 0);
        public void SetVbrConstraint(bool enabled) => API.opus_encoder_ctl(_encoder, Ctl.SetVbrConstraintRequest, enabled ? 1 : 0);
        public void SetComplexity(int complexity) => API.opus_encoder_ctl(_encoder, Ctl.SetComplexityRequest, complexity);
        public void SetSignalVoice() => API.opus_encoder_ctl(_encoder, Ctl.SetSignalRequest, 3001); //OPUS_SIGNAL_VOICE
        public void SetMaxBandwidthWideband() => API.opus_encoder_ctl(_encoder, Ctl.SetMaxBandwidthRequest, 1103); //OPUS_BANDWIDTH_WIDEBAND

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (_encoder != IntPtr.Zero)
            {
                API.opus_encoder_destroy(_encoder);
                _encoder = IntPtr.Zero;
            }
        }
    }

    internal class API
    {
        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out IntPtr error);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void opus_encoder_destroy(IntPtr encoder);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int opus_encode(IntPtr st, short[] pcm, int frame_size, IntPtr data, int max_data_bytes);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr opus_decoder_create(int Fs, int channels, out IntPtr error);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void opus_decoder_destroy(IntPtr decoder);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int opus_decode(IntPtr st, byte[] data, int len, IntPtr pcm, int frame_size, int decode_fec);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int opus_encoder_ctl(IntPtr st, Ctl request, int value);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int opus_encoder_ctl(IntPtr st, Ctl request, out int value);

        [DllImport("opus.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int opus_packet_pad(byte[] data, int len, int new_len);

    }

    public enum Ctl : int
    {
        SetBitrateRequest = 4002,
        GetBitrateRequest = 4003,
        SetMaxBandwidthRequest = 4004, //OPUS_SET_MAX_BANDWIDTH(x)
        SetVbrRequest = 4006, //OPUS_SET_VBR(x)
        SetComplexityRequest = 4010, //OPUS_SET_COMPLEXITY(x)
        SetInbandFECRequest = 4012,
        GetInbandFECRequest = 4013,
        SetVbrConstraintRequest = 4020, //OPUS_SET_VBR_CONSTRAINT(x)
        SetSignalRequest = 4024, //OPUS_SET_SIGNAL(x) (auto/voice/music)
    }

    public enum Errors
    {
        OK = 0,
        BadArg = -1,
        BufferToSmall = -2,
        InternalError = -3,
        InvalidPacket = -4,
        Unimplemented = -5,
        InvalidState = -6,
        AllocFail = -7
    }
}