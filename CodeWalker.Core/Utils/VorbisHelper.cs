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