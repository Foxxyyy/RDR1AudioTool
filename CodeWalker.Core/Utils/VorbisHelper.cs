using NAudio.Wave;
using OggVorbisSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodeWalker.Core.Utils
{
    public static unsafe class VorbisHelper
    {
        public static unsafe byte[] Decode(byte[] data, byte[] header1, byte[] header2, byte[] header3)
        {
            var dataPages = BufferSplit(data, 2048);

            vorbis_info info;
            vorbis_comment comment;
            vorbis_dsp_state state;
            vorbis_block vorbis_Block;

            Vorbis.vorbis_info_init(&info);
            Vorbis.vorbis_comment_init(&comment);

            var infoPacket = new ogg_packet();
            fixed (byte* bptr = header1)
            {
                infoPacket.packet = bptr;
            }

            infoPacket.bytes = new CLong(header1.Length);
            infoPacket.b_o_s = new CLong(256);
            infoPacket.e_o_s = new CLong(0);
            infoPacket.granulepos = -1;
            infoPacket.packetno = 0;

            if (Vorbis.vorbis_synthesis_headerin(&info, &comment, &infoPacket) == 1)
            {
                throw new Exception("Unable to process info header.");
            }

            var commentPacket = new ogg_packet();
            fixed (byte* bptr = header2)
            {
                commentPacket.packet = bptr;
            }
            commentPacket.bytes = new CLong(header2.Length);
            commentPacket.b_o_s = new CLong(0);
            commentPacket.e_o_s = new CLong(0);
            commentPacket.granulepos = -1;
            commentPacket.packetno = 0;

            if (Vorbis.vorbis_synthesis_headerin(&info, &comment, &commentPacket) == 1)
            {
                throw new Exception("Unable to process comment header.");
            }

            var codebookPacket = new ogg_packet();
            fixed (byte* bptr = header3)
            {
                codebookPacket.packet = bptr;
            }
            codebookPacket.bytes = new CLong(header3.Length);
            codebookPacket.b_o_s = new CLong(0);
            codebookPacket.e_o_s = new CLong(0);
            codebookPacket.granulepos = -1;
            codebookPacket.packetno = 0;

            if (Vorbis.vorbis_synthesis_headerin(&info, &comment, &codebookPacket) == 1)
            {
                throw new Exception("Unable to process codebook header.");
            }

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            if (Vorbis.vorbis_synthesis_init(&state, &info) == 0)
            {
                Vorbis.vorbis_block_init(&state, &vorbis_Block);
                foreach (byte[] page in dataPages)
                {
                    var ms2 = new MemoryStream(page);
                    var reader = new BinaryReader(ms2);
                    var packetsize = reader.ReadUInt16();

                    while (packetsize > 0)
                    {
                        var workingPacket = new ogg_packet
                        {
                            bytes = new CLong(packetsize)
                        };

                        var bytes = reader.ReadBytes(packetsize);
                        if (bytes.Length == 0)
                        {
                            break;
                        }

                        fixed (byte* bptr = bytes)
                        {
                            workingPacket.packet = bptr;
                        }

                        workingPacket.b_o_s = new CLong(0);
                        workingPacket.e_o_s = new CLong(0);
                        workingPacket.granulepos = -1;
                        workingPacket.packetno = 0;

                        if (Vorbis.vorbis_synthesis(&vorbis_Block, &workingPacket) == 0)
                        {
                            int ret = Vorbis.vorbis_synthesis(&vorbis_Block, &workingPacket);
                            Vorbis.vorbis_synthesis_blockin(&state, &vorbis_Block);

                            float** pcm;
                            int samples;

                            while ((samples = Vorbis.vorbis_synthesis_pcmout(&state, &pcm)) > 0)
                            {
                                var remaining = samples;
                                while (remaining > 0)
                                {
                                    var toProcess = Math.Min(remaining, 1024);
                                    int blockSize = sizeof(short) * toProcess * info.channels;
                                    short* result_ptr = stackalloc short[blockSize / sizeof(short)];

                                    for (int channel = 0; channel < info.channels; channel++)
                                    {
                                        short* current = result_ptr + channel;
                                        float* pcm_channel = pcm[channel];

                                        for (int position = 0; position < toProcess; position++)
                                        {
                                            int value = (int)(pcm_channel[position] * 32768.0f);
                                            if (value > 32767) value = 32767;
                                            if (value < -32768) value = -32768;

                                            *current = (short)value;
                                            current += info.channels;
                                        }
                                    }

                                    var resultBlock = new byte[blockSize];
                                    Marshal.Copy((IntPtr)result_ptr, resultBlock, 0, resultBlock.Length);
                                    writer.Write(resultBlock);

                                    Vorbis.vorbis_synthesis_read(&state, toProcess);
                                    remaining -= toProcess;
                                }
                            }
                        }
                        else
                        {
                            break;
                        }

                        if ((reader.BaseStream.Position % 2048 == 0) || reader.BaseStream.Position >= reader.BaseStream.Length)
                        {
                            packetsize = 0;
                            break;
                        }
                        packetsize = reader.ReadUInt16();
                    }
                }

                Vorbis.vorbis_block_clear(&vorbis_Block);
                Vorbis.vorbis_dsp_clear(&state);
            }
            else
            {
                throw new Exception("vorbis_synthesis_init failed");
            }

            Vorbis.vorbis_comment_clear(&comment);
            Vorbis.vorbis_info_clear(&info);

            return ms.ToArray();
        }

        private static byte[][] BufferSplit(byte[] buffer, int blockSize)
        {
            var blocks = new byte[(buffer.Length + blockSize - 1) / blockSize][];
            for (int i = 0, j = 0; i < blocks.Length; i++, j += blockSize)
            {
                blocks[i] = new byte[Math.Min(blockSize, buffer.Length - j)];
                Array.Copy(buffer, j, blocks[i], 0, blocks[i].Length);
            }
            return blocks;
        }

        public static unsafe byte[] Encode(byte[] data, long channels, long sampleRate, out byte[] streamIdData, out byte[] commentData, out byte[] codebookData)
        {
            var info = new vorbis_info();
            Vorbis.vorbis_info_init(&info);

            VorbisEnc.vorbis_encode_init_vbr(&info, channels, sampleRate, 0.5f);

            var comment = new vorbis_comment();
            Vorbis.vorbis_comment_init(&comment);

            var state = new vorbis_dsp_state();
            Vorbis.vorbis_analysis_init(&state, &info);

            var block = new vorbis_block();
            Vorbis.vorbis_block_init(&state, &block);

            var streamId = new ogg_packet();
            var commentPacket = new ogg_packet();
            var codebookPacket = new ogg_packet();
            Vorbis.vorbis_analysis_headerout(&state, &comment, &streamId, &commentPacket, &codebookPacket);

            streamIdData = new byte[streamId.bytes.Value];
            commentData = new byte[commentPacket.bytes.Value];
            codebookData = new byte[codebookPacket.bytes.Value];

            Marshal.Copy((IntPtr)streamId.packet, streamIdData, 0, (int)streamId.bytes.Value);
            Marshal.Copy((IntPtr)commentPacket.packet, commentData, 0, (int)commentPacket.bytes.Value);
            Marshal.Copy((IntPtr)codebookPacket.packet, codebookData, 0, (int)codebookPacket.bytes.Value);

            using var outputStream = new MemoryStream();
            using var bw = new BinaryWriter(outputStream);
            using var pcmStream = new MemoryStream(data);
            using var br = new BinaryReader(pcmStream);
            var pageBuffer = new List<byte>();

            void FlushPage()
            {
                while (pageBuffer.Count < 2048)
                {
                    pageBuffer.Add(0);
                }
                bw.Write(pageBuffer.ToArray());
                pageBuffer.Clear();
            }

            var endOfFile = false;
            while (!endOfFile)
            {
                var buffer = br.ReadBytes(4096);
                var samplesRead = buffer.Length / (2 * (int)channels);

                if (samplesRead == 0)
                {
                    Vorbis.vorbis_analysis_wrote(&state, 0);
                }
                else
                {
                    var inputBuffer = Vorbis.vorbis_analysis_buffer(&state, samplesRead);
                    for (int s = 0; s < samplesRead; s++)
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            int offset = (s * (int)channels + ch) * 2;
                            short sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                            inputBuffer[ch][s] = sample / 32768f;
                        }
                    }
                    Vorbis.vorbis_analysis_wrote(&state, samplesRead);
                }

                while (Vorbis.vorbis_analysis_blockout(&state, &block) == 1)
                {
                    Vorbis.vorbis_analysis(&block, null);
                    Vorbis.vorbis_bitrate_addblock(&block);

                    var dataOut = new ogg_packet();
                    while (Vorbis.vorbis_bitrate_flushpacket(&state, &dataOut) == 1)
                    {
                        var rawData = new byte[dataOut.bytes.Value];
                        Marshal.Copy((IntPtr)dataOut.packet, rawData, 0, rawData.Length);

                        int packetSize = rawData.Length + 2;
                        int remaining = 2048 - pageBuffer.Count;

                        //If it won't fit in this page, pad and flush
                        if (packetSize > remaining)
                        {
                            FlushPage();
                        }

                        // Now add the packet
                        pageBuffer.AddRange(BitConverter.GetBytes((ushort)rawData.Length));
                        pageBuffer.AddRange(rawData);

                        //If the page fills exactly, flush it immediately
                        if (pageBuffer.Count == 2048)
                        {
                            FlushPage();
                        }

                        if (samplesRead == 0)
                        {
                            endOfFile = true;
                        }
                    }
                }
            }

            // Final flush
            if (pageBuffer.Count > 0)
            {
                FlushPage();
            }

            var finalData = outputStream.ToArray();
            Vorbis.vorbis_block_clear(&block);
            Vorbis.vorbis_dsp_clear(&state);
            Vorbis.vorbis_comment_clear(&comment);
            Vorbis.vorbis_info_clear(&info);

            return finalData;
        }

        public static string GetVorbisVersion()
        {
            return Marshal.PtrToStringAnsi(VorbisEnc.vorbis_version_string());
        }
    }

    internal static unsafe class VorbisEnc
    {
        private const string LibraryName = "vorbis";

        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern int vorbis_encode_init_vbr(vorbis_info* vi, long channels, long rate, float base_quality);

        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern int vorbis_encode_init(vorbis_info* vi, long channels, long rate, long max_bitrate, long nominal_bitrate, long min_bitrate);

        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern IntPtr vorbis_version_string();
    }
}