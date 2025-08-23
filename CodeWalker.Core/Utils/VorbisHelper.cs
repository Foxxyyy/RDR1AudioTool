using OggVorbisSharp;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CodeWalker.Core.Utils
{
    internal static unsafe class VorbisHelper
    {
        private static void WritePacket(BinaryWriter bw, ogg_packet packet)
        {
            byte[] raw = new byte[packet.bytes.Value];
            Marshal.Copy((IntPtr)packet.packet, raw, 0, raw.Length);
            bw.Write((ushort)raw.Length);
            bw.Write(raw);
        }

        public static vorbis_dsp_state CreateHeader(long channels, long sampleRate, out byte[] streamIdData, out byte[] commentData, out byte[] codebookData)
        {
            MemoryStream memoryStream = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(memoryStream);

            vorbis_info info = new vorbis_info();
            Vorbis.vorbis_info_init(&info);
            VorbisEnc.vorbis_encode_init_vbr(&info, channels, sampleRate, 1);
            vorbis_dsp_state state = new vorbis_dsp_state();
            Vorbis.vorbis_analysis_init(&state, &info);
            vorbis_comment comment = new vorbis_comment();
            Vorbis.vorbis_comment_init(&comment);
            ogg_packet streamId = new ogg_packet();
            ogg_packet commentPacket = new ogg_packet();
            ogg_packet codebookPacket = new ogg_packet();
            Vorbis.vorbis_analysis_headerout(&state, &comment, &streamId, &commentPacket, &codebookPacket);

            streamIdData = new byte[streamId.bytes.Value];
            commentData = new byte[commentPacket.bytes.Value];
            codebookData = new byte[codebookPacket.bytes.Value];

            Marshal.Copy((IntPtr)streamId.packet, streamIdData, 0, (int)streamId.bytes.Value);
            Marshal.Copy((IntPtr)commentPacket.packet, commentData, 0, (int)commentPacket.bytes.Value);
            Marshal.Copy((IntPtr)codebookPacket.packet, codebookData, 0, (int)codebookPacket.bytes.Value);

            Vorbis.vorbis_comment_clear(&comment);
            Vorbis.vorbis_info_clear(&info);

            return state; 
        }

        public static unsafe byte[] Encode(byte[] data, long channels, long sampleRate, out byte[] streamIdData, out byte[] commentData, out byte[] codebookData)
        {
            var info = new vorbis_info();
            Vorbis.vorbis_info_init(&info);

            VorbisEnc.vorbis_encode_init(&info, channels, sampleRate, -1, 86000, -1); //Quality level 1.0f

            var comment = new vorbis_comment();
            Vorbis.vorbis_comment_init(&comment);

            var state = new vorbis_dsp_state();
            Vorbis.vorbis_analysis_init(&state, &info);

            var block = new vorbis_block();
            Vorbis.vorbis_block_init(&state, &block);

            var stream = new MemoryStream();
            var bw = new BinaryWriter(stream);

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

            var pcmStream = new MemoryStream(data);
            var br = new BinaryReader(pcmStream);
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
                            var offset = (int)((s * channels + ch) * 2);
                            var sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                            inputBuffer[ch][s] = sample / 32768f;
                        }
                    }
                    Vorbis.vorbis_analysis_wrote(&state, samplesRead); //Tells the library how much we actually submitted
                }

                while (Vorbis.vorbis_analysis_blockout(&state, &block) == 1) //Vorbis does some data preanalysis, then divvies up blocks
                {
                    Vorbis.vorbis_analysis(&block, null);
                    Vorbis.vorbis_bitrate_addblock(&block);

                    var dataOut = new ogg_packet();
                    while (Vorbis.vorbis_bitrate_flushpacket(&state, &dataOut) == 1)
                    {
                        var rawData = new byte[dataOut.bytes.Value];
                        Marshal.Copy((IntPtr)dataOut.packet, rawData, 0, rawData.Length);

                        //Write size + data
                        bw.Write((ushort)rawData.Length);
                        bw.Write(rawData);

                        if (samplesRead == 0)
                        {
                            endOfFile = true;
                        }
                    }
                }
            }

            br.Dispose();
            pcmStream.Dispose();

            var finalData = stream.ToArray();
            Vorbis.vorbis_block_clear(&block);
            Vorbis.vorbis_dsp_clear(&state);
            Vorbis.vorbis_comment_clear(&comment);
            Vorbis.vorbis_info_clear(&info);

            bw.Dispose();
            stream.Dispose();

            return finalData;
        }

        public static unsafe byte[] Decode(byte[] data, long channels, long sampleRate, byte[] streamIdData, byte[] commentData, byte[] codebookData)
        {
            ogg_packet streamId = new ogg_packet();
            fixed (byte* ptr = streamIdData)
            {
                streamId.packet = ptr;
            }
            streamId.bytes = new CLong(streamIdData.Length);
            streamId.b_o_s = new CLong(1);
            streamId.e_o_s = new CLong(0);
            streamId.granulepos = 0;
            streamId.packetno = 0;

            ogg_packet commentPacket = new ogg_packet();
            fixed (byte* ptr = commentData)
            {
                commentPacket.packet = ptr;
            }
            commentPacket.bytes = new CLong(commentData.Length);
            commentPacket.b_o_s = new CLong(0);
            commentPacket.e_o_s = new CLong(0);
            commentPacket.granulepos = 0;
            commentPacket.packetno = 1;

            ogg_packet codebookPacket = new ogg_packet();
            fixed (byte* ptr = codebookData)
            {
                codebookPacket.packet = ptr;
            }
            codebookPacket.bytes = new CLong(codebookData.Length);
            codebookPacket.b_o_s = new CLong(0);
            codebookPacket.e_o_s = new CLong(0);
            commentPacket.granulepos = 0;
            codebookPacket.packetno = 2;

            MemoryStream stream = new MemoryStream(data);
            BinaryReader reader = new BinaryReader(stream);
            
            while (stream.Position < stream.Length)
            {

            }

            vorbis_info vorbis_Info = new vorbis_info();
            vorbis_comment vorbis_Comment = new vorbis_comment();
            Vorbis.vorbis_info_init(&vorbis_Info);
            Vorbis.vorbis_comment_init(&vorbis_Comment);

            Vorbis.vorbis_synthesis_headerin(&vorbis_Info, &vorbis_Comment, &streamId);
            Vorbis.vorbis_synthesis_headerin(&vorbis_Info, &vorbis_Comment, &commentPacket);
            Vorbis.vorbis_synthesis_headerin(&vorbis_Info, &vorbis_Comment, &codebookPacket);

            vorbis_dsp_state state = new vorbis_dsp_state();
            vorbis_block block = new vorbis_block();
            if (Vorbis.vorbis_synthesis_init(&state, &vorbis_Info) == 0)
            {
                Vorbis.vorbis_block_init(&state, &block);
            }
            else
            {
                throw new Exception();
            }
            return null;
        }
    }

    internal static unsafe class VorbisEnc
    {
        private const string LibraryName = "vorbis";

        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern int vorbis_encode_init_vbr(vorbis_info* vi, long channels, long rate, float base_quality);

        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern int vorbis_encode_init(vorbis_info* vi, long channels, long rate, long max_bitrate, long nominal_bitrate, long min_bitrate);
    }
}