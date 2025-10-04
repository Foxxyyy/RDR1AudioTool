//Codewalker AWC File loader / writer code.

using CodeWalker.Core.Utils;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using EXP = System.ComponentModel.ExpandableObjectConverter;
using TC = System.ComponentModel.TypeConverterAttribute;

namespace CodeWalker.GameFiles
{
    [TC(typeof(EXP))]
    public class AwcFile
    {
        public string Name { get; set; }
        public string ErrorMessage { get; set; }
        public uint Magic { get; set; } = 0x54414441;
        public ushort Version { get; set; } = 1;
        public ushort Flags { get; set; } = 0xFF00;
        public int StreamCount { get; set; }
        public int DataOffset { get; set; }

        public bool ChunkIndicesFlag
        {
            get => (Flags & 1) == 1;
            set => Flags = (ushort)((Flags & 0xFFFE) + (value ? 1 : 0));
        }
        
        public bool SingleChannelEncryptFlag
        {
            get => (Flags & 2) == 2;
            set => Flags = (ushort)((Flags & 0xFFFD) + (value ? 2 : 0));
        }
        
        public bool MultiChannelFlag
        {
            get => (Flags & 4) == 4;
            set => Flags = (ushort)((Flags & 0xFFFB) + (value ? 4 : 0));
        }

        public bool MultiChannelEncryptFlag
        {
            get => (Flags & 8) == 8;
            set => Flags = (ushort)((Flags & 0xFFF7) + (value ? 8 : 0));
        }

        public ushort[] ChunkIndices { get; set; } //index of first chunk for each stream
        public AwcChunkInfo[] ChunkInfos { get; set; } // just for browsing convenience really
        public bool WholeFileEncrypted { get; set; }
        public AwcStreamInfo[] StreamInfos { get; set; }
        public AwcStream[] Streams { get; set; }
        public AwcStream MultiChannelSource { get; set; }
        public Dictionary<uint, AwcStream> StreamDict { get; set; }

        public static byte[] EncryptionKey = new byte[32]
        {
            0xB7, 0x62, 0xDF, 0xB6, 0xE2, 0xB2, 0xC6, 0xDE, 0xAF, 0x72, 0x2A, 0x32, 0xD2, 0xFB, 0x6F, 0x0C, 0x98, 0xA3, 0x21, 0x74, 0x62, 0xC9, 0xC4, 0xED, 0xAD, 0xAA, 0x2E, 0xD0, 0xDD, 0xF9, 0x2F, 0x10
        };

        public static void Decrypt_RSXXTEA(byte[] data, uint[] key)
        {
            // Rockstar's modified version of XXTEA
            uint[] blocks = new uint[data.Length / 4];
            Buffer.BlockCopy(data, 0, blocks, 0, data.Length);

            int block_count = blocks.Length;
            uint a, b = blocks[0], i;

            i = (uint)(0x9E3779B9 * (6 + 52 / block_count));
            do
            {
                for (int block_index = block_count - 1; block_index >= 0; --block_index)
                {
                    a = blocks[(block_index > 0 ? block_index : block_count) - 1];
                    b = blocks[block_index] -= (a >> 5 ^ b << 2) + (b >> 3 ^ a << 4) ^ (i ^ b) + (key[block_index & 3 ^ (i >> 2 & 3)] ^ a ^ 0x7B3A207F);
                }
                i -= 0x9E3779B9;
            } while (i != 0);

            Buffer.BlockCopy(blocks, 0, data, 0, data.Length);
        }

        public void Load(byte[] data, string entry)
        {
            Name = entry;
            if (!string.IsNullOrEmpty(Name))
            {
                var nl = Name.ToLowerInvariant();
                var fn = Path.GetFileNameWithoutExtension(nl);
                JenkIndex.Ensure(fn + "_left");
                JenkIndex.Ensure(fn + "_right");
            }

            if ((data == null) || (data.Length < 8))
            {
                ErrorMessage = "Data null or too short!";
                return; //nothing to do, not enough data...
            }

            Magic = BitConverter.ToUInt32(data, 0);
            if (Magic != 0x54414441 && Magic != 0x41444154)
            {
                if (data.Length % 4 == 0)
                {
                    throw new NotSupportedException("File is encrypted, contact the developer for help.");
                }
                else
                {
                    ErrorMessage = "Corrupted data!";
                }
            }

            Endianess endianess;
            switch (Magic)
            {
                default:
                    ErrorMessage = "Unexpected Magic 0x" + Magic.ToString("X");
                    return;
                case 0x54414441:
                    endianess = Endianess.LittleEndian;
                    break;
                case 0x41444154:
                    endianess = Endianess.BigEndian;
                    break;
            }

            using var ms = new MemoryStream(data);
            var r = new DataReader(ms, endianess);
            Read(r);
        }

        public byte[] Save()
        {
            var s = new MemoryStream();
            var w = new DataWriter(s);
            Write(w);

            var buf = new byte[s.Length];
            s.Position = 0;
            s.Read(buf, 0, buf.Length);
            return buf;
        }

        private void Read(DataReader r)
        {
            Magic = r.ReadUInt32();
            Version = r.ReadUInt16();
            Flags = r.ReadUInt16();
            StreamCount = r.ReadInt32();
            DataOffset = r.ReadInt32();

            if (ChunkIndicesFlag)
            {
                ChunkIndices = new ushort[StreamCount]; //index of first chunk for each stream
                for (int i = 0; i < StreamCount; i++)
                {
                    ChunkIndices[i] = r.ReadUInt16();
                }
            }

            var infos = new List<AwcStreamInfo>();
            var chunks = new List<AwcChunkInfo>();

            for (int i = 0; i < StreamCount; i++)
            {
                var info = new AwcStreamInfo();
                info.Read(r);
                infos.Add(info);
            }

            for (int i = 0; i < StreamCount; i++)
            {
                var info = infos[i];
                for (int j = 0; j < info.ChunkCount; j++)
                {
                    var chunk = new AwcChunkInfo();
                    chunk.Read(r);
                    chunks.Add(chunk);
                    info.Chunks[j] = chunk;
                }
            }

            StreamInfos = infos.ToArray();
            ChunkInfos = chunks.ToArray();

            var streams = new List<AwcStream>();
            for (int i = 0; i < StreamCount; i++)
            {
                var info = StreamInfos[i];
                var stream = new AwcStream(info, this);
                stream.Read(r);
                streams.Add(stream);

                if (MultiChannelFlag && (stream.DataChunk != null))
                {
                    MultiChannelSource = stream;
                }
            }

            Streams = streams.Where(s => s.DataChunk != null).ToArray();
            MultiChannelSource?.AssignMultiChannelSources(Streams);
            BuildStreamDict();
            TestChunkOrdering();
        }

        private void Write(DataWriter w)
        {
            StreamCount = StreamInfos?.Length ?? 0;
            var infoStart = 16 + (ChunkIndicesFlag ? (StreamCount * 2) : 0);
            var dataOffset = infoStart + StreamCount * 4;

            foreach (var info in StreamInfos)
            {
                dataOffset += (int)info.ChunkCount * 8;
            }

            var chunks = GetSortedChunks();
            DataOffset = dataOffset;

            w.Write(Magic);
            w.Write(Version);
            w.Write(Flags);
            w.Write(StreamCount);
            w.Write(DataOffset);

            if (ChunkIndicesFlag)
            {
                for (int i = 0; i < StreamCount; i++)
                {
                    w.Write((i < (ChunkIndices?.Length ?? 0)) ? ChunkIndices[i] : (ushort)0);
                }
            }

            //Write stream infos
            for (int i = 0; i < StreamCount; i++)
            {
                var info = StreamInfos[i];
                info.Write(w);
            }

            //Write chunk infos
            for (int i = 0; i < StreamCount; i++)
            {
                var info = StreamInfos[i];
                for (int j = 0; j < info.ChunkCount; j++)
                {
                    var chunkinfo = info.Chunks[j];
                    chunkinfo.Write(w);
                }
            }

            //Write chunk data
            foreach (var chunk in chunks)
            {
                var chunkinfo = chunk.ChunkInfo;
                var align = chunkinfo.Align;

                if (align > 0)
                {
                    var padc = (align - (w.Position % align)) % align;
                    if (padc > 0) w.Write(new byte[padc]);
                }
                chunk.Write(w);
            }
        }

        public void WriteXml(StringBuilder sb, int indent, string audioFolder)
        {
            AwcXml.ValueTag(sb, indent, "Version", Version.ToString());
            if (ChunkIndicesFlag)
            {
                AwcXml.ValueTag(sb, indent, "ChunkIndices", true.ToString());
            }
            if (MultiChannelFlag)
            {
                AwcXml.ValueTag(sb, indent, "MultiChannel", true.ToString());
            }
            if ((Streams?.Length ?? 0) > 0)
            {
                AwcXml.OpenTag(sb, indent, "Streams");
                var strlist = Streams.ToList();
                strlist.Sort((a, b) => a.Name.CompareTo(b.Name));
                foreach (var stream in strlist)
                {
                    AwcXml.OpenTag(sb, indent + 1, "Item");
                    stream.WriteXml(sb, indent + 2, audioFolder);
                    AwcXml.CloseTag(sb, indent + 1, "Item");
                }
                AwcXml.CloseTag(sb, indent, "Streams");
            }
        }

        public void ReadXml(XmlNode node, string wavfolder)
        {
            Version = (ushort)Xml.GetChildUIntAttribute(node, "Version");
            ChunkIndicesFlag = Xml.GetChildBoolAttribute(node, "ChunkIndices");
            MultiChannelFlag = Xml.GetChildBoolAttribute(node, "MultiChannel");

            var snode = node.SelectSingleNode("Streams");
            if (snode != null)
            {
                var slist = new List<AwcStream>();
                var inodes = snode.SelectNodes("Item");
                foreach (XmlNode inode in inodes)
                {
                    var stream = new AwcStream(this);
                    stream.ReadXml(inode, wavfolder);
                    slist.Add(stream);

                    if (MultiChannelFlag && (stream.StreamFormatChunk != null || stream.VorbisFormatChunk != null) && stream.Hash == 0)
                    {
                        MultiChannelSource = stream;
                    }
                }

                Streams = slist.ToArray();
                StreamCount = Streams?.Length ?? 0;
                MultiChannelSource?.CompactMultiChannelSources(Streams);
            }

            BuildPeakChunks();
            BuildChunkIndices();
            BuildStreamInfos();
            BuildStreamDict();
        }

        public static void WriteXmlNode(AwcFile f, StringBuilder sb, int indent, string wavfolder, string name = "AudioWaveContainer")
        {
            if (f == null) return;
            AwcXml.OpenTag(sb, indent, name);
            f.WriteXml(sb, indent + 1, wavfolder);
            AwcXml.CloseTag(sb, indent, name);
        }

        public static AwcFile ReadXmlNode(XmlNode node, string wavfolder)
        {
            if (node == null) return null;
            var f = new AwcFile();
            f.ReadXml(node, wavfolder);
            return f;
        }

        public unsafe void ReplaceAudioStreamStereo(MetaHash leftHash, MetaHash rightHash, uint sampleCount, uint sampleRate, byte[] pcmAudioData, AwcCodecType targetCodec)
        {
            var totalBytes = pcmAudioData.Length; //Must have a length multiple of 4
            var leftData = new byte[pcmAudioData.Length / 2];
            var rightData = new byte[pcmAudioData.Length / 2];
            var position = 0;

            for (int n = 0; n < totalBytes; n += 4)
            {
                //L sample
                leftData[position] = pcmAudioData[n];
                leftData[position + 1] = pcmAudioData[n + 1];

                //R sample
                rightData[position] = pcmAudioData[n + 2];
                rightData[position + 1] = pcmAudioData[n + 3];
                position += 2;
            }

            byte[] leftStreamIdData = null;
            byte[] leftCommentData = null;
            byte[] leftCodebookData = null;

            byte[] rightStreamIdData = null;
            byte[] rightCommentData = null;
            byte[] rightCodebookData = null;

            var leftStream = Streams.FirstOrDefault(s => s.Hash == leftHash);
            var rightStream = Streams.FirstOrDefault(s => s.Hash == rightHash);

            if (leftStream == null || rightStream == null)
            {
                return;
            }

            if (targetCodec == AwcCodecType.ADPCM || targetCodec == AwcCodecType.PCM)
            {
                if (targetCodec == AwcCodecType.ADPCM)
                {
                    leftData = AdpcmHelper.Encode(leftData, (int)sampleCount);
                    rightData = AdpcmHelper.Encode(rightData, (int)sampleCount);
                }
                ReplacePCM(ref rightStream, sampleCount, sampleRate, rightData, targetCodec, MultiChannelFlag);
                ReplacePCM(ref leftStream, sampleCount, sampleRate, leftData, targetCodec, MultiChannelFlag);
            }
            else if (targetCodec == AwcCodecType.VORBIS)
            {
                leftData = VorbisHelper.Encode(leftData, 1, sampleRate, out leftStreamIdData, out leftCommentData, out leftCodebookData);
                rightData = VorbisHelper.Encode(rightData, 1, sampleRate, out rightStreamIdData, out rightCommentData, out rightCodebookData);

                ReplaceVorbis(ref leftStream, sampleCount, sampleRate, leftData, leftStreamIdData, leftCommentData, leftCodebookData, MultiChannelFlag);
                ReplaceVorbis(ref rightStream, sampleCount, sampleRate, rightData, rightStreamIdData, rightCommentData, rightCodebookData, MultiChannelFlag);
            }

            //Push updated streams back
            for (int i = 0; i < Streams.Length; i++)
            {
                if (Streams[i].Hash == leftHash) Streams[i] = leftStream;
                if (Streams[i].Hash == rightHash) Streams[i] = rightStream;
            }
        }

        public void ReplaceAudioStreamSingle(MetaHash streamHash, uint sampleCount, uint sampleRate, byte[] pcmAudioData, AwcCodecType targetCodec)
        {
            if (targetCodec == AwcCodecType.ADPCM)
            {
                pcmAudioData = AdpcmHelper.Encode(pcmAudioData, (int)sampleCount);
            }
            else if (targetCodec == AwcCodecType.MSADPCM)
            {
                var blockAlign = 70;
                var pcmSamples = new short[pcmAudioData.Length / 2];

                Buffer.BlockCopy(pcmAudioData, 0, pcmSamples, 0, pcmAudioData.Length);
                var adpcmData = MsAdpcmHelper.EncodeMSADPCMMono(pcmSamples, blockAlign);

                var numChannels = 1; //Make sure it's always 1
                var bitsPerSample = 4;
                var samplesPerBlock = (blockAlign - 7 * numChannels) * 2 / numChannels + 2;
                var bytesPerSecond = (int)(sampleRate * blockAlign / (double)samplesPerBlock);

                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);

                //RIFF header
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(0); //Placeholder
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                //Format chunk
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(0x32); //Chunk size
                bw.Write((short)WaveFormatEncoding.Adpcm); //MSADPCM = 2
                bw.Write((short)numChannels);
                bw.Write(sampleRate);
                bw.Write(bytesPerSecond);
                bw.Write((short)blockAlign);
                bw.Write((short)bitsPerSample);
                bw.Write((short)32); //cbSize = 32
                bw.Write((short)samplesPerBlock);
                bw.Write((short)7); //Coef count

                for (int i = 0; i < 7; i++)
                {
                    bw.Write(MsAdpcmHelper.Coefs[i, 0]);
                    bw.Write(MsAdpcmHelper.Coefs[i, 1]);
                }

                //Data chunk
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(adpcmData.Length);

                //Encoded data
                bw.Write(adpcmData);

                //Patch RIFF size
                bw.Seek(4, SeekOrigin.Begin);
                bw.Write((int)(ms.Length - 8));

                pcmAudioData = ms.ToArray();
            }
            else if (targetCodec == AwcCodecType.VORBIS)
            {
                throw new NotImplementedException("Vorbis support is not implemented for single streams, use PCM/ADPCM for RDR2 or MSADPCM for RDR1");
            }
            else if (targetCodec == AwcCodecType.OPUS)
            {
                var pcmSamples = new short[pcmAudioData.Length / 2];
                Buffer.BlockCopy(pcmAudioData, 0, pcmSamples, 0, pcmAudioData.Length);

                var frameSamples = 960; //20ms @ 48kHz
                var frameSizeBytes = 80;
                var frames = new List<byte[]>();

                using var encoder = OpusEncoder.Create((int)sampleRate, 1); //2 for stereo
                encoder.SetBitrate(64000);

                for (int pos = 0; pos < pcmSamples.Length; pos += frameSamples)
                {
                    var len = Math.Min(frameSamples, pcmSamples.Length - pos);
                    var frame = new short[len * 2];
                    Array.Copy(pcmSamples, pos, frame, 0, len);

                    var packet = encoder.Encode(frame, len);
                    if (packet.Length < frameSizeBytes)
                    {
                        Array.Resize(ref packet, frameSizeBytes); //Pad
                    }
                    frames.Add(packet);
                }

                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);

                //Block header (per channel, 0x10)
                bw.Write((uint)0);                              //start_entry
                bw.Write((uint)frames.Count);                   //entries
                bw.Write((uint)0);                              //channel_skip
                bw.Write((uint)(frames.Count * frameSamples));  //channel_samples

                //Extra header (0x70, D11A)
                bw.Write(0x41313144);                    //"D11A"
                bw.Write((ushort)frameSizeBytes);        //Bytes per packet
                bw.Write((ushort)frameSamples);          //PCM samples per frame
                bw.Write((ushort)0x0101);                //OPUS flags
                bw.Write((ushort)sampleRate);            //Sample rate
                bw.Write(0x45313144);                    //"D11E"

                for (int i = 0x10; i < 0x70; i++)
                {
                    bw.Write((byte)0x77);
                }

                //Payload
                foreach (var f in frames)
                {
                    bw.Write(f);
                }
                pcmAudioData = ms.ToArray();
            }

            AwcStream targetStream = null;
            foreach (AwcStream stream in Streams)
            {
                if (stream.Hash == streamHash)
                {
                    targetStream = stream;
                    break;
                }
            }

            if (targetStream == null)
            {
                throw new InvalidOperationException($"Unable to find stream {streamHash}");
            }

            switch (targetCodec)
            {
                case AwcCodecType.PCM:
                case AwcCodecType.ADPCM:
                case AwcCodecType.MSADPCM:
                    ReplacePCM(ref targetStream, sampleCount, sampleRate, pcmAudioData, targetCodec, MultiChannelFlag);
                    break;
                case AwcCodecType.VORBIS:
                    throw new NotImplementedException("Vorbis support is not implemented");
                case AwcCodecType.OPUS:
                    throw new NotImplementedException("OPUS support is not implemented");
                default:
                    throw new NotImplementedException("This codec is not implemented");
            }
        }

        public static void ReplacePCM(ref AwcStream stream, uint sampleCount, uint sampleRate, byte[] pcmAudioData, AwcCodecType codecType, bool isMultiChannel = false)
        {
            if (stream.VorbisChunk != null)
            {
                stream.VorbisChunk = null;
            }

            if (!isMultiChannel)
            {
                var fmtChunk = (AwcFormatChunk)stream.Chunks.Where(c => c.GetType() == typeof(AwcFormatChunk)).FirstOrDefault();
                if (fmtChunk != null)
                {
                    fmtChunk.Samples = sampleCount;
                    fmtChunk.SamplesPerSecond = (ushort)sampleRate;
                    fmtChunk.Codec = codecType;
                }
            }
            else
            {
                stream.StreamFormat.Samples = sampleCount;
                stream.StreamFormat.SamplesPerSecond = (ushort)sampleRate;
                stream.StreamFormat.Codec = codecType;
            }

            stream.DataChunk ??= new AwcDataChunk(new AwcChunkInfo() { Type = AwcChunkType.data });
            stream.DataChunk.Data = pcmAudioData;
        }

        public static void ReplaceVorbis(ref AwcStream stream, uint sampleCount, uint sampleRate, byte[] audioData, byte[] streamIdData, byte[] commentData, byte[] codebookData, bool isMultiChannel = false)
        {
            //Remove old format chunk
            var vorbisFormat = stream.VorbisFormatChunk != null;
            if (stream.FormatChunk != null)
                stream.FormatChunk = null;
            if (vorbisFormat)
                stream.VorbisFormatChunk = null;

            var chunks = stream.Chunks.ToList();
            stream.Chunks = null;

            //Drop any old format chunks
            chunks.RemoveAll(c => c is AwcFormatChunk);

            //Update format info
            if (!isMultiChannel)
            {
                if (vorbisFormat)
                {
                    var vorbisChunk = new AwcVorbisChunk(new AwcChunkInfo() { Type = AwcChunkType.vorbisheader })
                    {
                        DataSection1 = streamIdData,
                        DataSection2 = commentData,
                        DataSection3 = codebookData
                    };

                    stream.VorbisFormatChunk ??= new AwcVorbisFormatChunk(new AwcChunkInfo() { Type = AwcChunkType.format_vorbis })
                    {
                        LoopPoint = -1,
                        Unk2 = -1,
                        Samples = sampleCount,
                        SamplesPerSecond = (ushort)sampleRate,
                        VorbisChunk = vorbisChunk,
                        Codec = AwcCodecType.VORBIS
                    };
                    chunks.Add(stream.VorbisFormatChunk);
                }
                else
                {
                    stream.FormatChunk ??= new AwcFormatChunk(new AwcChunkInfo() { Type = AwcChunkType.format })
                    {
                        LoopPoint = -1,
                        Samples = sampleCount,
                        SamplesPerSecond = (ushort)sampleRate,
                        Codec = AwcCodecType.VORBIS
                    };
                    chunks.Add(stream.FormatChunk);
                }
            }
            else
            {
                stream.StreamFormat.Samples = sampleCount;
                stream.StreamFormat.SamplesPerSecond = (ushort)sampleRate;
                stream.StreamFormat.Codec = AwcCodecType.VORBIS;
            }

            //Replace or add Vorbis header chunk
            if (stream.VorbisChunk == null)
            {
                stream.VorbisChunk = new AwcVorbisChunk(new AwcChunkInfo() { Type = AwcChunkType.vorbisheader })
                {
                    DataSection1 = streamIdData,
                    DataSection2 = commentData,
                    DataSection3 = codebookData
                };
                chunks.Add(stream.VorbisChunk);
            }

            stream.Chunks = chunks.ToArray();
            stream.DataChunk ??= new AwcDataChunk(new AwcChunkInfo() { Type = AwcChunkType.data });
            stream.DataChunk.Data = audioData;
        }

        public AwcChunk[] GetSortedChunks()
        {
            var chunks = new List<AwcChunk>();
            if (Streams != null)
            {
                foreach (var stream in Streams)
                {
                    if (stream.Chunks != null)
                    {
                        chunks.AddRange(stream.Chunks);
                    }
                }
            }

            var issorted = MultiChannelFlag || !SingleChannelEncryptFlag;
            if (issorted)
            {
                chunks.Sort((a, b) => b.ChunkInfo?.SortOrder.CompareTo(a.ChunkInfo?.SortOrder ?? 0) ?? -1);
                chunks.Reverse();
            }
            return chunks.ToArray();
        }

        public void TestChunkOrdering()
        {
            if (Streams == null) return;
            if (StreamInfos == null) return;

            var issorted = MultiChannelFlag || !SingleChannelEncryptFlag;
            var chunklist = ChunkInfos.ToList();
            chunklist.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var chunks = chunklist.ToArray();

            var infoStart = 16 + (ChunkIndicesFlag ? (StreamCount * 2) : 0);
            var offset = infoStart + (StreamCount * 4) + (chunks.Length * 8);

            foreach (var chunk in chunks)
            {
                if (issorted)
                {
                    var align = chunk.Align;
                    if (align != 0)
                    {
                        offset += ((align - (offset % align)) % align);
                    }
                }
                switch (chunk.Type)
                {
                    case AwcChunkType.animation:
                    case AwcChunkType.rave_anim_data:
                        switch (chunk.Offset - offset)
                        {
                            case 12:
                            case 8:
                            case 0:
                            case 4:
                                break;

                            default:
                                break;//no hit
                        }
                        offset = chunk.Offset;//what is correct padding for this? seems to be inconsistent
                        break;

                    case AwcChunkType.gesture:
                        switch (chunk.Offset - offset)
                        {
                            case 0:
                            case 2:
                                break;

                            default:
                                break;//no hit
                        }
                        offset = chunk.Offset;//what is correct padding for this? seems to be inconsistent
                        break;

                    case AwcChunkType.seektable:
                        break;
                }
                offset += chunk.Size;
            }

            if (WholeFileEncrypted) offset += (4 - (offset % 4)) % 4;

            if (issorted)
            {
                bool datachunk = false;
                bool markerchunk = false;
                foreach (var chunk in chunks)
                {
                    if ((chunk.Type == AwcChunkType.data) || (chunk.Type == AwcChunkType.mid)) datachunk = true;
                    else if (datachunk)
                    { }//no hit
                    if ((chunk.Type == AwcChunkType.markers) || (chunk.Type == AwcChunkType.granulargrains) || (chunk.Type == AwcChunkType.granularloops)) markerchunk = true;
                    else if (markerchunk && !datachunk)
                    { }//no hit
                }
            }
            else
            {
                if (ChunkInfos[0].Type != AwcChunkType.data)
                { }//no hit
            }
        }

        public void BuildPeakChunks()
        {
            if (Streams == null) return;
            foreach (var stream in Streams)
            {
                if (stream.FormatChunk == null || stream.VorbisFormatChunk == null) continue; //Peak chunk is always null here
                if (stream.FormatChunk.Peak == null || stream.VorbisFormatChunk.Peak == null) continue; //Rare, only in boar_near.awc

                var pcmData = stream.GetPcmData();
                var codec = stream.VorbisFormatChunk?.Codec ?? stream.FormatChunk.Codec;
                var smpCount = stream.VorbisFormatChunk?.Samples ?? stream.FormatChunk.Samples;
                var peakSize = 4096;
                var peakCount = (smpCount - peakSize) / peakSize;
                var peakvals = new ushort[peakCount];

                ushort getSample(int i)
                {
                    var ei = i * 2;
                    if ((ei + 2) >= pcmData.Length) return 0;
                    var smp = BitConverter.ToInt16(pcmData, i * 2);
                    return (ushort)Math.Min(Math.Abs((int)smp) * 2, 65535);
                }
                ushort getPeak(int n)
                {
                    var o = n * peakSize;
                    var p = (ushort)0;
                    for (int i = 0; i < peakSize; i++)
                    {
                        var s = getSample(i + o);
                        p = Math.Max(p, s);
                    }
                    return p;
                }

                var peak = getPeak(0);
                if (stream.VorbisFormatChunk != null)
                    stream.VorbisFormatChunk.PeakVal = peak;
                else
                    stream.FormatChunk.PeakVal = peak;

                for (int n = 0; n < peakCount; n++)
                {
                    var peakn = getPeak(n + 1);
                    peakvals[n] = peakn;
                }

                if (stream.PeakChunk == null)
                {
                    if (peakvals.Length > 0)
                    {
                        //need to add a new peak chunk for the extra data (could happen on XML import)
                        var chunk = AwcStream.CreateChunk(new AwcChunkInfo() { Type = AwcChunkType.peak });
                        var chunklist = stream.Chunks?.ToList() ?? new List<AwcChunk>();
                        chunklist.Add(chunk);
                        stream.Chunks = chunklist.ToArray();
                        stream.PeakChunk = chunk as AwcPeakChunk;
                    }
                }
                if (stream.PeakChunk != null)
                {
                    if (peakvals.Length > 0)
                    {
                        stream.PeakChunk.Data = peakvals;
                    }
                    else
                    {
                        //remove any unneeded peak chunk (could happen on XML import)
                        var chunklist = stream.Chunks?.ToList() ?? new List<AwcChunk>();
                        chunklist.Remove(stream.PeakChunk);
                        stream.Chunks = chunklist.ToArray();
                        stream.PeakChunk = null;
                    }
                }
            }
        }

        public void BuildChunkIndices()
        {
            if (Streams == null) return;

            var inds = new List<ushort>();
            ushort ind = 0;

            foreach (var stream in Streams)
            {
                inds.Add(ind);
                ind += (ushort)(stream.Chunks?.Length ?? 0);
            }

            if (ChunkIndicesFlag)
                ChunkIndices = inds.ToArray();
            else
                ChunkIndices = null;
        }

        public void BuildStreamInfos()
        {
            var streaminfos = new List<AwcStreamInfo>();
            var chunkinfos = new List<AwcChunkInfo>();

            if (Streams != null)
            {
                var streamCount = Streams.Length;
                var infoStart = 16 + (ChunkIndicesFlag ? (streamCount * 2) : 0);
                var dataOffset = infoStart + streamCount * 4;

                foreach (var stream in Streams)
                {
                    dataOffset += (stream?.Chunks?.Length ?? 0) * 8;
                }

                var chunks = GetSortedChunks();
                foreach (var chunk in chunks)
                {
                    var chunkinfo = chunk.ChunkInfo;
                    var size = chunk.ChunkSize;
                    var align = chunkinfo.Align;

                    if (align > 0)
                    {
                        var padc = (align - (dataOffset % align)) % align;
                        dataOffset += padc;
                    }

                    chunkinfo.Size = size;
                    chunkinfo.Offset = dataOffset;
                    dataOffset += size;
                }

                foreach (var stream in Streams)
                {
                    var streaminfo = stream.StreamInfo;
                    streaminfos.Add(streaminfo);
                    chunkinfos.Clear();

                    if (stream.Chunks != null)
                    {
                        foreach (var chunk in stream.Chunks)
                        {
                            var chunkinfo = chunk.ChunkInfo;
                            chunkinfos.Add(chunkinfo);
                        }
                    }
                    streaminfo.Chunks = chunkinfos.ToArray();
                    streaminfo.ChunkCount = (uint)chunkinfos.Count;
                }
            }
            StreamInfos = streaminfos.ToArray();
        }

        public void BuildStreamDict()
        {
            StreamDict = new Dictionary<uint, AwcStream>();
            if (Streams == null) return;

            foreach (var stream in Streams)
            {
                StreamDict[stream.Hash] = stream;
            }
        }
    }

    public enum AwcCodecType
    {
        PCM = 0,  //PCM 16-bit little endian
        ADPCM = 4, //IMA PC
        VORBIS = 8, //RDR2 PC
        OPUS = 13, //RDR1 PC
        MSADPCM = 17 //RDR1 PC
    }

    [TC(typeof(EXP))]
    public class AwcStreamInfo
    {
        public uint RawVal { get; set; }
        public uint ChunkCount { get; set; }
        public MetaHash Id { get; set; }
        public AwcChunkInfo[] Chunks { get; set; }

        public void Read(DataReader r)
        {
            RawVal = r.ReadUInt32();
            ChunkCount = RawVal >> 29;
            Id = RawVal & 0x1FFFFFFF;
            Chunks = new AwcChunkInfo[ChunkCount];
        }

        public void Write(DataWriter w)
        {
            ChunkCount = (uint)(Chunks?.Length ?? 0);
            RawVal = (Id & 0x1FFFFFFF) + (ChunkCount << 29);
            w.Write(RawVal);
        }

        public override string ToString()
        {
            return Id.ToString() + ": " + ChunkCount.ToString() + " chunks";
        }
    }

    [TC(typeof(EXP))]
    public class AwcChunkInfo
    {
        public ulong RawVal { get; set; }
        public AwcChunkType Type { get; set; }
        public int Size { get; set; }
        public int Offset { get; set; }

        public int SortOrder
        {
            get
            {
                switch (Type)
                {
                    case AwcChunkType.data:
                        return 5;
                    case AwcChunkType.mid:
                        return 3;
                    case AwcChunkType.markers:
                    case AwcChunkType.granulargrains:
                    case AwcChunkType.granularloops:
                    case AwcChunkType.animation:
                    case AwcChunkType.rave_anim_data:
                    case AwcChunkType.gesture:
                        return 2;
                    case AwcChunkType.seektable:
                        return 0;
                    case AwcChunkType.peak:
                    case AwcChunkType.format:
                    case AwcChunkType.streamformat:
                    case AwcChunkType.format_vorbis:
                        return 1;
                    case AwcChunkType.vorbisheader:
                        return 4;
                    default:
                        break;
                }
                return 0;
            }
        }

        public int Align
        {
            get
            {
                switch (Type)
                {
                    case AwcChunkType.data:
                    case AwcChunkType.mid:
                        return 16;
                    case AwcChunkType.markers:
                    case AwcChunkType.granulargrains:
                    case AwcChunkType.granularloops:
                        return 4;
                    default:
                        break;
                }
                return 0;
            }
        }

        public void Read(DataReader r)
        {
            RawVal = r.ReadUInt64();
            Type = (AwcChunkType)(RawVal >> 56);
            Size = (int)((RawVal >> 28) & 0x0FFFFFFF);
            Offset = (int)(RawVal & 0x0FFFFFFF);
        }

        public void Write(DataWriter w)
        {
            RawVal = (((ulong)Offset) & 0x0FFFFFFF) + ((((ulong)Size) & 0x0FFFFFFF) << 28) + (((ulong)Type) << 56);
            w.Write(RawVal);
        }

        public override string ToString()
        {
            return Type.ToString() + ": " + Size.ToString() + ", " + Offset.ToString();
        }
    }

    [TC(typeof(EXP))]
    public class AwcStream
    {
        public AwcFile Awc { get; set; }
        public AwcStreamInfo StreamInfo { get; set; }
        public AwcChunk[] Chunks { get; set; }
        public AwcFormatChunk FormatChunk { get; set; }
        public AwcDataChunk DataChunk { get; set; }
        public AwcAnimationChunk AnimationChunk { get; set; }
        public AwcGestureChunk GestureChunk { get; set; }
        public AwcPeakChunk PeakChunk { get; set; }
        public AwcMIDIChunk MidiChunk { get; set; }
        public AwcMarkersChunk MarkersChunk { get; set; }
        public AwcGranularGrainsChunk GranularGrainsChunk { get; set; }
        public AwcGranularLoopsChunk GranularLoopsChunk { get; set; }
        public AwcStreamFormatChunk StreamFormatChunk { get; set; }
        public AwcSeekTableChunk SeekTableChunk { get; set; }
        public AwcVorbisChunk VorbisChunk { get; set; }
        public AwcVorbisFormatChunk VorbisFormatChunk { get; set; }
        public AwcStream[] ChannelStreams { get; set; }
        public AwcStream StreamSource { get; set; }
        public AwcStreamFormat StreamFormat { get; set; }
        public AwcStreamDataBlock[] StreamBlocks { get; set; }
        public int StreamChannelIndex { get; set; }

        public int SamplesPerSecond
        {
            get
            {
                return FormatChunk?.SamplesPerSecond ?? StreamFormat?.SamplesPerSecond ?? StreamFormatChunk?.Channels[0]?.SamplesPerSecond ?? VorbisFormatChunk?.SamplesPerSecond ?? 0;
            }
        }

        public int SampleCount
        {
            get
            {
                return (int)(FormatChunk?.Samples ?? StreamFormat?.Samples ?? StreamFormatChunk?.Channels[0]?.Samples ?? VorbisFormatChunk?.Samples ?? 0);
            }
        }

        public MetaHash Hash
        {
            get
            {
                return StreamInfo?.Id ?? 0;
            }
            set
            {
                if (StreamInfo != null)
                {
                    StreamInfo.Id = value & 0x1FFFFFFF;
                }
            }
        }

        public MetaHash HashAdjusted
        {
            get
            {
                var h = (uint)Hash;
                if (h == 0) return h;
                for (uint i = 0; i < 8; i++)
                {
                    var th = h + (i << 29);
                    if (!string.IsNullOrEmpty(JenkIndex.TryGetString(th))) return th;
                    if (MetaNames.TryGetString(th, out string str)) return th;
                }
                return h;
            }
        }

        public string Name
        {
            get
            {
                if (CachedName != null) return CachedName;
                var ha = HashAdjusted;
                var str = JenkIndex.TryGetString(ha);
                if (!string.IsNullOrEmpty(str)) CachedName = str;
                else if (MetaNames.TryGetString(ha, out str)) CachedName = str;
                else CachedName = "0x" + Hash.Hex;
                return CachedName;
            }
            set
            {
                CachedName = null;
                JenkIndex.Ensure(value);
                if (value.StartsWith("0x"))
                {
                    Hash = (uint)new System.ComponentModel.UInt32Converter().ConvertFromString(value);
                }
                else
                {
                    Hash = JenkHash.GenHash(value);
                }

                if (StreamFormat != null)
                {
                    StreamFormat.Id = Hash;
                }
            }
        }

        private string CachedName;

        public string TypeString
        {
            get
            {
                if (MidiChunk != null)
                {
                    return "MIDI";
                }

                var fc = AwcCodecType.PCM;
                var hz = 0;

                if (FormatChunk != null)
                {
                    fc = FormatChunk.Codec;
                    hz = FormatChunk.SamplesPerSecond;
                }

                if (StreamFormat != null)
                {
                    fc = StreamFormat.Codec;
                    hz = StreamFormat.SamplesPerSecond;
                }

                if (StreamFormatChunk != null)
                {
                    var channel = StreamFormatChunk.Channels[0];
                    fc = channel.Codec;
                    hz = channel.SamplesPerSecond;
                }

                if (VorbisFormatChunk != null)
                {
                    fc = VorbisFormatChunk.Codec;
                    hz = VorbisFormatChunk.SamplesPerSecond;
                }

                string codec = fc.ToString();
                switch (fc)
                {
                    case AwcCodecType.PCM:
                    case AwcCodecType.ADPCM:
                    case AwcCodecType.VORBIS:
                    case AwcCodecType.MSADPCM:
                    case AwcCodecType.OPUS:
                        break;
                    default:
                        codec = "Unknown";
                        break;
                }
                return codec + ((hz > 0) ? (", " + hz.ToString() + " Hz") : "");
            }
        }

        public float Length
        {
            get
            {
                if (FormatChunk != null) return (float)FormatChunk.Samples / FormatChunk.SamplesPerSecond;
                if (VorbisFormatChunk != null) return (float)VorbisFormatChunk.Samples / VorbisFormatChunk.SamplesPerSecond;
                if (StreamFormat != null) return (float)StreamFormat.Samples / StreamFormat.SamplesPerSecond;
                if ((StreamFormatChunk != null) && (StreamFormatChunk.Channels?.Length > 0))
                {
                    var chan = StreamFormatChunk.Channels[0];
                    return (float)chan.Samples / chan.SamplesPerSecond;
                }
                return 0;
            }
        }

        public string LengthStr
        {
            get
            {
                var ts = TimeSpan.FromSeconds(Length);
                if (ts.TotalSeconds < 60.0)
                    return ts.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
                else
                    return $"{(int)ts.TotalMinutes}m{ts.Seconds:00}s";
            }
        }

        public int ByteLength
        {
            get
            {
                if (MidiChunk?.Data != null) return MidiChunk.Data.Length;
                if (DataChunk?.Data != null) return DataChunk.Data.Length;

                if (StreamSource != null)
                {
                    int c = 0;
                    if (StreamSource?.StreamBlocks != null)
                    {
                        foreach (var blk in StreamSource.StreamBlocks)
                        {
                            if (StreamChannelIndex < (blk?.Channels?.Length ?? 0))
                            {
                                var chan = blk.Channels[StreamChannelIndex];
                                c += chan?.Data?.Length ?? 0;
                            }
                        }
                    }
                    return c;
                }
                return 0;
            }
        }

        public AwcStream(AwcFile awc)
        {
            Awc = awc;
            StreamInfo = new AwcStreamInfo();
        }

        public AwcStream(AwcStreamInfo s, AwcFile awc)
        {
            Awc = awc;
            StreamInfo = s;
        }

        public void Read(DataReader r)
        {
            var chunklist = new List<AwcChunk>();
            for (int j = 0; j < StreamInfo.Chunks?.Length; j++)
            {
                var cinfo = StreamInfo.Chunks[j];
                r.Position = cinfo.Offset;

                var chunk = CreateChunk(cinfo);
                chunk?.Read(r);
                chunklist.Add(chunk);
            }
            Chunks = chunklist.ToArray();
            ExpandChunks();
            DecodeData(r.Endianess);
        }

        public void Write(DataWriter w)
        {
            //not used since chunks are collected and sorted, then written separately
        }

        public void WriteXml(StringBuilder sb, int indent, string wavfolder)
        {
            AwcXml.StringTag(sb, indent, "Name", AwcXml.HashString(HashAdjusted));
            if (Hash != 0) //skip the wave file output for multichannel sources
            {
                var export = !string.IsNullOrEmpty(wavfolder);
                var fname = Name?.Replace("/", "")?.Replace("\\", "") ?? "0x0";
                byte[] fdata = null;
                if (MidiChunk != null)
                {
                    fname += ".midi";
                    fdata = export ? MidiChunk.Data : null;
                }
                else if (VorbisChunk != null)
                {
                    fname += ".ogg";
                    fdata = export ? GetOggFile() : null;
                }
                else
                {
                    fname += ".wav";
                    fdata = export ? GetWavFile() : null;
                }
                AwcXml.StringTag(sb, indent, "FileName", AwcXml.XmlEscape(fname));
                try
                {
                    if (export)
                    {
                        if (!Directory.Exists(wavfolder))
                        {
                            Directory.CreateDirectory(wavfolder);
                        }
                        var filepath = Path.Combine(wavfolder, fname);
                        File.WriteAllBytes(filepath, fdata);
                    }
                }
                catch
                { }
            }
            if (StreamFormat != null)
            {
                AwcXml.OpenTag(sb, indent, "StreamFormat");
                StreamFormat.WriteXml(sb, indent + 1);
                AwcXml.CloseTag(sb, indent, "StreamFormat");
            }
            if ((Chunks?.Length ?? 0) > 0)
            {
                AwcXml.OpenTag(sb, indent, "Chunks");
                for (int i = 0; i < Chunks.Length; i++)
                {
                    var chunk = Chunks[i];
                    if (chunk == null) continue;

                    AwcXml.OpenTag(sb, indent + 1, "Item");
                    Chunks[i].WriteXml(sb, indent + 2);
                    AwcXml.CloseTag(sb, indent + 1, "Item");
                }
                AwcXml.CloseTag(sb, indent, "Chunks");
            }
        }

        public void ReadXml(XmlNode node, string wavfolder)
        {
            Hash = XmlMeta.GetHash(Xml.GetChildInnerText(node, "Name"));
            var fnode = node.SelectSingleNode("StreamFormat");
            if (fnode != null)
            {
                StreamFormat = new AwcStreamFormat();
                StreamFormat.ReadXml(fnode);
                StreamFormat.Id = Hash;
            }
            var cnode = node.SelectSingleNode("Chunks");
            if (cnode != null)
            {
                var clist = new List<AwcChunk>();
                var inodes = cnode.SelectNodes("Item");
                foreach (XmlNode inode in inodes)
                {
                    var type = Xml.GetChildEnumInnerText<AwcChunkType>(inode, "Type");
                    var info = new AwcChunkInfo() { Type = type };
                    var chunk = CreateChunk(info);
                    chunk?.ReadXml(inode);
                    clist.Add(chunk);
                }
                Chunks = clist.ToArray();
            }

            ExpandChunks();

            var filename = Xml.GetChildInnerText(node, "FileName")?.Replace("/", "")?.Replace("\\", "");
            if (!string.IsNullOrEmpty(filename) && !string.IsNullOrEmpty(wavfolder))
            {
                try
                {
                    var filepath = Path.Combine(wavfolder, filename);
                    if (File.Exists(filepath))
                    {
                        var fdata = File.ReadAllBytes(filepath);
                        if (MidiChunk != null)
                        {
                            MidiChunk.Data = fdata;
                        }
                        else
                        {
                            if (filepath.EndsWith(".wav"))
                            {
                                ParseWavFile(fdata);
                            }
                            else if (filepath.EndsWith(".ogg"))
                            {
                                ParseOggFile(fdata);
                            }
                            else
                            {
                                throw new InvalidOperationException("Audio file is not supported.");
                            }
                        }
                    }
                }
                catch
                { }
            }
        }

        public static AwcChunk CreateChunk(AwcChunkInfo info)
        {
            return info.Type switch
            {
                AwcChunkType.data => new AwcDataChunk(info),
                AwcChunkType.format => new AwcFormatChunk(info),
                AwcChunkType.peak => new AwcPeakChunk(info),
                AwcChunkType.mid => new AwcMIDIChunk(info),
                AwcChunkType.gesture => new AwcGestureChunk(info),
                AwcChunkType.granulargrains => new AwcGranularGrainsChunk(info),
                AwcChunkType.granularloops => new AwcGranularLoopsChunk(info),
                AwcChunkType.markers => new AwcMarkersChunk(info),
                AwcChunkType.streamformat => new AwcStreamFormatChunk(info),
                AwcChunkType.seektable => new AwcSeekTableChunk(info),
                AwcChunkType.vorbisheader => new AwcVorbisChunk(info),
                AwcChunkType.format_vorbis => new AwcVorbisFormatChunk(info),
                AwcChunkType.animation or AwcChunkType.rave_anim_data => new AwcAnimationChunk(info),
                _ => null,
            };
        }

        public void ExpandChunks()
        {
            if (Chunks != null)
            {
                foreach (var chunk in Chunks)
                {
                    if (chunk is AwcDataChunk dataChunk) DataChunk = dataChunk;
                    if (chunk is AwcFormatChunk formatChunk) FormatChunk = formatChunk;
                    if (chunk is AwcMarkersChunk markersChunk) MarkersChunk = markersChunk;
                    if (chunk is AwcGestureChunk gestureChunk) GestureChunk = gestureChunk;
                    if (chunk is AwcPeakChunk peakChunk) PeakChunk = peakChunk;
                    if (chunk is AwcMIDIChunk midiChunk) MidiChunk = midiChunk;
                    if (chunk is AwcStreamFormatChunk streamformatChunk) StreamFormatChunk = streamformatChunk;
                    if (chunk is AwcSeekTableChunk seektableChunk) SeekTableChunk = seektableChunk;
                    if (chunk is AwcGranularGrainsChunk ggChunk) GranularGrainsChunk = ggChunk;
                    if (chunk is AwcGranularLoopsChunk glChunk) GranularLoopsChunk = glChunk;
                    if (chunk is AwcVorbisChunk vorbisChunk) VorbisChunk = vorbisChunk;
                    if (chunk is AwcVorbisFormatChunk vorbisFormatChunk) VorbisFormatChunk = vorbisFormatChunk;
                    if (chunk is AwcAnimationChunk animChunk) AnimationChunk = animChunk;
                }
            }
        }

        public void DecodeData(Endianess endianess)
        {
            //Create multichannel blocks and decrypt data where necessary
            if (DataChunk?.Data != null)
            {
                if (Awc.MultiChannelFlag)
                {
                    var ocount = (int)(SeekTableChunk?.SeekTable?.Length ?? 0);
                    var ccount = (int)(StreamFormatChunk?.ChannelCount ?? 0);
                    var bcount = (int)(StreamFormatChunk?.BlockCount ?? 0);
                    var blocksize = (int)(StreamFormatChunk?.BlockSize ?? 0);
                    var blist = new List<AwcStreamDataBlock>();

                    for (int b = 0; b < bcount; b++)
                    {
                        int srcoff = b * blocksize;
                        int mcsoff = (b < ocount) ? (int)SeekTableChunk.SeekTable[b] : 0;
                        int blen = Math.Max(Math.Min(blocksize, DataChunk.Data.Length - srcoff), 0);
                        var bdat = new byte[blen];
                        Buffer.BlockCopy(DataChunk.Data, srcoff, bdat, 0, blen);

                        if (Awc.MultiChannelEncryptFlag && !Awc.WholeFileEncrypted)
                        {
                            throw new NotSupportedException("File is encrypted, contact the developer for help.");
                            //AwcFile.Decrypt_RSXXTEA(bdat, GTA5Keys.PC_AWC_KEY);
                        }
                        var blk = new AwcStreamDataBlock(bdat, StreamFormatChunk, endianess, mcsoff);
                        blist.Add(blk);
                    }
                    StreamBlocks = blist.ToArray();
                }
                else
                {
                    if (Awc.SingleChannelEncryptFlag && !Awc.WholeFileEncrypted)
                    {
                        //AwcFile.Decrypt_RSXXTEA(DataChunk.Data, AwcFile.EncryptionKey);
                    }
                }
            }
        }

        public void AssignMultiChannelSources(AwcStream[] streams)
        {
            var cstreams = new List<AwcStream>();
            for (int i = 0; i < (streams?.Length ?? 0); i++)
            {
                var stream = streams[i];
                if (stream != this)
                {
                    var id = stream.StreamInfo?.Id ?? 0;
                    var srcind = 0;
                    var chancnt = StreamFormatChunk?.Channels?.Length ?? 0;

                    for (int ind = 0; ind < chancnt; ind++)
                    {
                        var mchan = StreamFormatChunk.Channels[ind];
                        if (mchan.Id == id)
                        {
                            srcind = ind;
                            break;
                        }
                    }

                    stream.StreamSource = this;
                    stream.StreamFormat = (srcind < chancnt) ? StreamFormatChunk.Channels[srcind] : null;
                    stream.StreamChannelIndex = srcind;
                    cstreams.Add(stream);
                }
            }
            ChannelStreams = cstreams.ToArray();
        }

        public void CompactMultiChannelSources(AwcStream[] streams)
        {
            var chanlist = new List<AwcStreamFormat>();
            var chandatas = new List<byte[]>();

            for (int i = 0; i < (streams?.Length ?? 0); i++)
            {
                var stream = streams[i];
                if (stream != this)
                {
                    chanlist.Add(stream.StreamFormat);
                    chandatas.Add(stream.DataChunk?.Data);
                }
            }

            StreamFormatChunk.Channels = chanlist.ToArray();
            StreamFormatChunk.ChannelCount = (uint)chanlist.Count;

            //Figure out how many smaller blocks fit in the larger block
            var chancount = chanlist.Count;
            var blocksize = (int)(StreamFormatChunk?.BlockSize ?? 1032192);
            var hdrsize = 96 * chancount + (blocksize / 512) + 1024;
            hdrsize += (0x800 - (hdrsize % 0x800)) % 0x800;
            var smblockspace = (blocksize - hdrsize) / 2048;
            var smblockcount = smblockspace / chancount;

            //Split the channel datas into their blocks
            var streamblocks = new List<AwcStreamDataBlock>();
            var seektable = new List<uint>();
            var chansmpoffs = new List<int>();

            for (int c = 0; c < chancount; c++)
            {
                var chaninfo = chanlist[c];
                var chandata = chandatas[c];
                var cdlen = chandata?.Length ?? 0;
                var totsmblockcount = (cdlen / 2048) + (((cdlen % 2048) != 0) ? 1 : 0);
                var totlgblockcount = (totsmblockcount / smblockcount) + (((totsmblockcount % smblockcount) != 0) ? 1 : 0);

                for (int i = streamblocks.Count; i < totlgblockcount; i++)
                {
                    var blk = new AwcStreamDataBlock
                    {
                        ChannelInfo = StreamFormatChunk,
                        Channels = new AwcStreamDataChannel[chancount],
                        ChannelCount = (uint)chancount,
                        SampleOffset = i * smblockcount * 4088
                    };
                    streamblocks.Add(blk);
                    seektable.Add((uint)blk.SampleOffset);
                }

                chansmpoffs.Clear();
                var samplesrem = (int)chaninfo.Samples + 1;

                for (int i = 0; i < totsmblockcount; i++)
                {
                    chansmpoffs.Add(i * 4088);
                    var blkcnt = chansmpoffs.Count;

                    if ((blkcnt == smblockcount) || (i == totsmblockcount - 1))
                    {
                        var lgblockind = i / smblockcount;
                        var blkstart = lgblockind * smblockcount;
                        var blk = streamblocks[lgblockind];
                        
                        var chan = new AwcStreamDataChannel(StreamFormatChunk, c);
                        var smpcnt = smblockcount * 4088;
                        var bytcnt = smblockcount * 2048;
                        var srcoff = blkstart * 2048;
                        var srccnt = Math.Min(bytcnt, cdlen - srcoff);
                        
                        var data = new byte[bytcnt];
                        if (srccnt > 0)
                        {
                            Buffer.BlockCopy(chandata, srcoff, data, 0, srccnt);
                        }

                        chan.Data = data;
                        chan.SampleOffsets = chansmpoffs.ToArray();
                        chan.SampleCount = Math.Min(smpcnt, samplesrem);
                        chan.BlockCount = smblockcount;
                        chan.StartBlock = c * smblockcount;
                        
                        blk.Channels[c] = chan;
                        chansmpoffs.Clear();
                        samplesrem -= smpcnt;
                    }
                }
            }

            StreamBlocks = streamblocks.ToArray();
            StreamFormatChunk.BlockCount = (uint)streamblocks.Count;

            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    if (stream.SeekTableChunk != null)
                    {
                        stream.SeekTableChunk.SeekTable = seektable.ToArray();
                    }

                    if (stream.StreamFormatChunk != null)
                    {
                        stream.StreamFormatChunk.BlockCount = StreamFormatChunk.BlockCount;
                        stream.StreamFormatChunk.BlockSize = StreamFormatChunk.BlockSize;
                        stream.StreamFormatChunk.ChannelCount = StreamFormatChunk.ChannelCount;
                        stream.StreamFormatChunk.Channels = StreamFormatChunk.Channels;
                    }
                }
            }

            //Build stream blocks into final data chunk
            var ms = new MemoryStream();
            var w = new DataWriter(ms);

            foreach (var blk in streamblocks)
            {
                var start = w.Position;
                blk.Write(w);

                var offs = w.Position - start;
                var padc = blocksize - offs;

                if (padc > 0)
                {
                    w.Write(new byte[padc]);
                }
            }

            var bytes = new byte[ms.Length];
            ms.Position = 0;
            ms.Read(bytes, 0, (int)ms.Length);

            DataChunk ??= new AwcDataChunk(new AwcChunkInfo() { Type = AwcChunkType.data });
            DataChunk.Data = bytes;
        }

        public override string ToString()
        {
            var hash = StreamInfo?.Id.ToString() + ": ";
            if (FormatChunk != null)
                return hash + FormatChunk?.ToString() ?? "AwcAudio";
            if (VorbisFormatChunk != null)
                return hash + VorbisFormatChunk?.ToString() ?? "AwcAudio";
            if (StreamFormat != null)
                return hash + StreamFormat?.ToString() ?? "AwcAudio";
            if (MidiChunk != null)
                return hash + MidiChunk.ToString();
            return hash + "Unknown";
        }

        public byte[] GetRawData()
        {
            if (StreamFormat != null)
            {
                if (DataChunk?.Data == null)
                {
                    var ms = new MemoryStream();
                    var bw = new BinaryWriter(ms);

                    if (StreamSource?.StreamBlocks != null)
                    {
                        foreach (var blk in StreamSource.StreamBlocks)
                        {
                            if (StreamChannelIndex < (blk?.Channels?.Length ?? 0))
                            {
                                var chan = blk.Channels[StreamChannelIndex];
                                var cdata = chan.Data;
                                bw.Write(cdata);
                            }
                        }
                    }

                    bw.Flush();
                    ms.Position = 0;

                    DataChunk = new AwcDataChunk(null)
                    {
                        Data = new byte[ms.Length]
                    };
                    ms.Read(DataChunk.Data, 0, (int)ms.Length);
                }
            }
            return DataChunk.Data;
        }

        public byte[] GetPcmData()
        {
            var data = GetRawData();
            var codec = StreamFormat?.Codec ?? FormatChunk?.Codec ?? StreamFormatChunk?.Channels[0]?.Codec ?? VorbisFormatChunk?.Codec ?? AwcCodecType.PCM;

            switch (codec)
            {
                case AwcCodecType.ADPCM: //Just convert ADPCM to PCM for compatibility reasons
                    return AdpcmHelper.Decode(data, SampleCount);
                case AwcCodecType.MSADPCM:
                    return MsAdpcmHelper.Decode(data, SampleCount);
                case AwcCodecType.OPUS:
                    return OpusHelper.Decode(data, (int)StreamFormatChunk.ChannelCount, StreamFormatChunk.BlockCount, SamplesPerSecond);
                case AwcCodecType.VORBIS:
                    if (VorbisFormatChunk != null)
                        return VorbisHelper.Decode(data, VorbisFormatChunk.VorbisChunk.DataSection1, VorbisFormatChunk.VorbisChunk.DataSection2, VorbisFormatChunk.VorbisChunk.DataSection3);
                    else
                        return VorbisHelper.Decode(data, VorbisChunk.DataSection1, VorbisChunk.DataSection2, VorbisChunk.DataSection3);
            }
            return data;
        }

        public byte[] GetWavFile()
        {
            var ms = GetWavStream();
            var data = new byte[ms.Length];
            ms.Read(data, 0, (int)ms.Length);
            return data;
        }

        public Stream GetWavStream()
        {
            byte[] dataPCM = GetPcmData();
            var bitsPerSample = 32;
            var channels = 1;
            short formatcodec = 1; // 1 = WAVE_FORMAT_PCM
            int byteRate = SamplesPerSecond * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);
            short samplesPerBlock = 0;
            bool addextrafmt = false;

            //if (codec == AwcCodecFormat.ADPCM)//can't seem to get ADPCM wav files to work :(
            //{
            //    bitsPerSample = 4;
            //    formatcodec = 17;
            //    byteRate = (int)(SamplesPerSecond * 0.50685 * channels);
            //    blockAlign = 2048;// (short)(256 * (4 * channels));// (short)(36 * channels);//256;// 2048;//
            //    samplesPerBlock = 4088;// (short)(((blockAlign - (4 * channels)) * 8) / (bitsPerSample * channels) + 1); // 2044;//
            //    addextrafmt = true;
            //}

            MemoryStream stream = new MemoryStream();
            BinaryWriter w = new BinaryWriter(stream);
            int wavLength = 36 + dataPCM.Length;
            if (addextrafmt)
            {
                wavLength += 4;
            }

            // RIFF chunk
            w.Write("RIFF".ToCharArray());
            w.Write((int)wavLength);
            w.Write("WAVE".ToCharArray());

            // fmt sub-chunk
            w.Write("fmt ".ToCharArray());
            w.Write((int)(addextrafmt ? 20 : 16)); // fmt size
            w.Write((short)formatcodec);
            w.Write((short)channels);
            w.Write((int)SamplesPerSecond);
            w.Write((int)byteRate);
            w.Write((short)blockAlign);
            w.Write((short)bitsPerSample);
            if (addextrafmt)
            {
                w.Write((ushort)0x0002);
                w.Write((ushort)samplesPerBlock);
            }

            // data sub-chunk
            w.Write("data".ToCharArray());
            w.Write((int)dataPCM.Length);
            w.Write(dataPCM);

            w.Flush();
            stream.Position = 0;
            return stream;
        }

        public void ParseWavFile(byte[] wav)
        {
            using var ms = new MemoryStream(wav);
            using WaveFileReader reader = new WaveFileReader(ms);
            var pcmData = new byte[reader.Length];
            reader.Read(pcmData, 0, (int)reader.Length);

            if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                throw new InvalidOperationException("Only PCM format .wav files supported!");
            if (reader.WaveFormat.Channels != 1)
                throw new InvalidOperationException("Only mono .wav files supported!");

            var sampleCount = pcmData.Length / (reader.WaveFormat.BitsPerSample / 8);
            var codec = StreamFormat?.Codec ?? FormatChunk?.Codec ?? VorbisFormatChunk?.Codec ?? AwcCodecType.PCM;

            if (codec == AwcCodecType.ADPCM && reader.WaveFormat.Encoding != WaveFormatEncoding.Adpcm) //Convert PCM wav to ADPCM where required
            {
                switch (reader.WaveFormat.BitsPerSample)
                {
                    case 8:
                        throw new InvalidOperationException("Please encode PCM as 16 bit signed before importing or choose PCM instead.");
                    case 16:
                        pcmData = AdpcmHelper.Encode(pcmData, sampleCount);
                        break;

                    case 32:
                        throw new InvalidOperationException("Please encode PCM as 16 bit signed before importing or choose PCM instead.");
                }
            }

            if (Awc.MultiChannelFlag)
            {
                if (StreamFormat != null)
                {
                    StreamFormat.Samples = (uint)sampleCount;
                    StreamFormat.SamplesPerSecond = (ushort)reader.WaveFormat.SampleRate;

                    DataChunk = new AwcDataChunk(null)
                    {
                        Data = pcmData
                    };
                }
            }
            else
            {
                if (FormatChunk != null)
                {
                    FormatChunk.Samples = (uint)sampleCount;
                    FormatChunk.SamplesPerSecond = (ushort)reader.WaveFormat.SampleRate;
                }

                if (VorbisFormatChunk != null)
                {
                    VorbisFormatChunk.Samples = (uint)sampleCount;
                    VorbisFormatChunk.SamplesPerSecond = (ushort)reader.WaveFormat.SampleRate;
                }

                DataChunk ??= new AwcDataChunk(new AwcChunkInfo() { Type = AwcChunkType.data });
                DataChunk.Data = pcmData;
            }
        }

        public byte[] GetOggFile()
        {
            var ms = GetOggStream();
            var data = new byte[ms.Length];
            ms.Read(data, 0, (int)ms.Length);
            return data;
        }

        public unsafe Stream GetOggStream()
        {
            var dataVorbis = GetRawData();
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);

            w.Write(VorbisChunk.DataSection1.Length);
            w.Write(VorbisChunk.DataSection1);
            w.Write(VorbisChunk.DataSection2.Length);
            w.Write(VorbisChunk.DataSection2);
            w.Write(VorbisChunk.DataSection3.Length);
            w.Write(VorbisChunk.DataSection3);
            w.Write(dataVorbis);

            stream.Position = 0;
            return stream;
        }

        public void ParseOggFile(byte[] ogg)
        {
            var oggStream = new MemoryStream(ogg);
            var reader = new BinaryReader(oggStream);
            var length = reader.ReadInt32();
            var data1 = reader.ReadBytes(length);

            var data1Stream = new MemoryStream(data1);
            var data1reader = new BinaryReader(data1Stream);
            data1Stream.Position += 7;
            var version = data1reader.ReadInt32();
            var channels = data1reader.ReadByte();
            var sampleRate = data1reader.ReadInt32();

            var length2 = reader.ReadInt32();
            var data2 = reader.ReadBytes(length2);
            var length3 = reader.ReadInt32();
            var data3 = reader.ReadBytes(length3);

            VorbisChunk.DataSection1 = data1;
            VorbisChunk.DataSection2 = data2;
            VorbisChunk.DataSection3 = data3;

            if (Awc.MultiChannelFlag)
            {
                if (StreamFormat != null)
                {
                    StreamFormat.SamplesPerSecond = (ushort)sampleRate;
                    DataChunk = new AwcDataChunk(null)
                    {
                        Data = reader.ReadBytes((int)(oggStream.Length - oggStream.Position))
                    };
                }
            }
            else
            {
                if (FormatChunk != null)
                {
                    FormatChunk.SamplesPerSecond = (ushort)sampleRate;
                }

                if (VorbisFormatChunk != null)
                {
                    VorbisFormatChunk.SamplesPerSecond = (ushort)sampleRate;
                }
                DataChunk ??= new AwcDataChunk(new AwcChunkInfo() { Type = AwcChunkType.data });
                DataChunk.Data = reader.ReadBytes((int)(oggStream.Length - oggStream.Position));
            }
        }
    }

    public enum AwcChunkType : byte
    {
        data = 0x55,            // 0x5EB5E655
        format = 0xFA,          // 0x6061D4FA
        animation = 0x5C,       // 0x938C925C
        peak = 0x36,            // 0x8B946236
        mid = 0x68,             // 0x71DE4C68
        gesture = 0x2B,         // 0x23097A2B
        granulargrains = 0x5A,  // 0xE787895A
        granularloops = 0xD9,   // 0x252C20D9
        markers = 0xBD,         // 0xD4CB98BD
        streamformat = 0x48,    // 0x81F95048
        seektable = 0xA3,       // 0x021E86A3
        vorbisheader = 0x7F,    // 0x20D0EF7F
        format_vorbis = 0x76,   // 0xA4609776
        rave_anim_data = 0x81   // 0xC8140E81
    }

    [TC(typeof(EXP))]
    public abstract class AwcChunk : IMetaXmlItem
    {
        public abstract int ChunkSize { get; }
        public AwcChunkInfo ChunkInfo { get; set; }

        public AwcChunk(AwcChunkInfo info)
        {
            ChunkInfo = info;
        }

        public abstract void Read(DataReader r);

        public abstract void Write(DataWriter w);

        public abstract void WriteXml(StringBuilder sb, int indent);

        public abstract void ReadXml(XmlNode node);
    }

    [TC(typeof(EXP))]
    public class AwcDataChunk : AwcChunk
    {
        public override int ChunkSize => Data?.Length ?? 0;
        public byte[] Data { get; set; }

        public AwcDataChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            Data = r.ReadBytes(ChunkInfo.Size);
        }

        public override void Write(DataWriter w)
        {
            w.Write(Data);
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            //this is just a placeholder. in XML, channel data is written as WAV files
        }

        public override void ReadXml(XmlNode node)
        {
        }

        public override string ToString()
        {
            return "data: " + (Data?.Length ?? 0).ToString() + " bytes";
        }
    }

    [TC(typeof(EXP))]
    public class AwcFormatChunk : AwcChunk
    {
        public override int ChunkSize => Peak.HasValue ? 24 : 20;

        public uint Samples { get; set; }
        public int LoopPoint { get; set; }
        public ushort SamplesPerSecond { get; set; }
        public short Headroom { get; set; }
        public ushort LoopBegin { get; set; }
        public ushort LoopEnd { get; set; }
        public ushort PlayEnd { get; set; }
        public byte PlayBegin { get; set; }
        public AwcCodecType Codec { get; set; }
        public uint? Peak { get; set; }

        public ushort PeakVal
        {
            get => (ushort)((Peak ?? 0) & 0xFFFF);
            set => Peak = ((Peak ?? 0) & 0xFFFF0000) + value;
        }

        public ushort PeakUnk
        {
            get => (ushort)((Peak ?? 0) >> 16);
            set => Peak = ((Peak ?? 0) & 0xFFFF) + (ushort)(value << 16);
        }

        public AwcFormatChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            Samples = r.ReadUInt32();
            LoopPoint = r.ReadInt32();
            SamplesPerSecond = r.ReadUInt16();
            Headroom = r.ReadInt16();
            LoopBegin = r.ReadUInt16();
            LoopEnd = r.ReadUInt16();
            PlayEnd = r.ReadUInt16();
            PlayBegin = r.ReadByte();
            Codec = (AwcCodecType)r.ReadByte();

            switch (ChunkInfo.Size)
            {
                case 20:
                    break;
                case 24:
                    Peak = r.ReadUInt32();
                    break;
                default:
                    break;
            }
        }

        public override void Write(DataWriter w)
        {
            w.Write(Samples);
            w.Write(LoopPoint);
            w.Write(SamplesPerSecond);
            w.Write(Headroom);
            w.Write(LoopBegin);
            w.Write(LoopEnd);
            w.Write(PlayEnd);
            w.Write(PlayBegin);
            w.Write((byte)Codec);

            if (Peak.HasValue)
            {
                w.Write(Peak.Value);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.StringTag(sb, indent, "Codec", Codec.ToString());
            AwcXml.ValueTag(sb, indent, "Samples", Samples.ToString());
            AwcXml.ValueTag(sb, indent, "SampleRate", SamplesPerSecond.ToString());
            AwcXml.ValueTag(sb, indent, "Headroom", Headroom.ToString());
            AwcXml.ValueTag(sb, indent, "PlayBegin", PlayBegin.ToString());
            AwcXml.ValueTag(sb, indent, "PlayEnd", PlayEnd.ToString());
            AwcXml.ValueTag(sb, indent, "LoopBegin", LoopBegin.ToString());
            AwcXml.ValueTag(sb, indent, "LoopEnd", LoopEnd.ToString());
            AwcXml.ValueTag(sb, indent, "LoopPoint", LoopPoint.ToString());

            if (Peak.HasValue)
            {
                AwcXml.ValueTag(sb, indent, "Peak", PeakUnk.ToString(), "unk");
            }
        }

        public override void ReadXml(XmlNode node)
        {
            Codec = Xml.GetChildEnumInnerText<AwcCodecType>(node, "Codec");
            Samples = Xml.GetChildUIntAttribute(node, "Samples");
            SamplesPerSecond = (ushort)Xml.GetChildUIntAttribute(node, "SampleRate");
            Headroom = (short)Xml.GetChildIntAttribute(node, "Headroom");
            PlayBegin = (byte)Xml.GetChildUIntAttribute(node, "PlayBegin");
            PlayEnd = (ushort)Xml.GetChildUIntAttribute(node, "PlayEnd");
            LoopBegin = (ushort)Xml.GetChildUIntAttribute(node, "LoopBegin");
            LoopEnd = (ushort)Xml.GetChildUIntAttribute(node, "LoopEnd");
            LoopPoint = Xml.GetChildIntAttribute(node, "LoopPoint");
            var pnode = node.SelectSingleNode("Peak");
            if (pnode != null)
            {
                PeakUnk = (ushort)Xml.GetUIntAttribute(pnode, "unk");
            }
        }

        public override string ToString()
        {
            return "format: " + Codec.ToString() + ": " + Samples.ToString() + " samples, " + SamplesPerSecond.ToString() + " samples/sec, headroom: " + Headroom.ToString();
        }
    }

    [TC(typeof(EXP))]
    public class AwcVorbisFormatChunk : AwcChunk
    {
        public override int ChunkSize => 32 + VorbisHeaderLength;
        public uint Samples { get; set; }
        public int LoopPoint { get; set; }
        public ushort SamplesPerSecond { get; set; }
        public short Headroom { get; set; }
        public ushort Unk1 { get; set; }
        public ushort LoopBegin { get; set; }
        public ushort LoopEnd { get; set; }
        public ushort PlayEnd { get; set; }
        public ushort PlayBegin { get; set; }
        public int Unk2 { get; set; }
        public ushort? Peak { get; set; }
        public AwcCodecType Codec { get; set; }
        public ushort VorbisHeaderLength { get; set; }
        public AwcVorbisChunk VorbisChunk { get; set; }

        public ushort PeakVal
        {
            get => (ushort)((Peak ?? 0) & 0xFFFF);
            set => Peak = (ushort?)(((Peak ?? 0) & 0xFFFF0000) + value);
        }

        public ushort PeakUnk
        {
            get => (ushort)((Peak ?? 0) >> 16);
            set => Peak = (ushort?)(((Peak ?? 0) & 0xFFFF) + (ushort)(value << 16));
        }

        public AwcVorbisFormatChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            Samples = r.ReadUInt32();
            LoopPoint = r.ReadInt32();
            SamplesPerSecond = r.ReadUInt16();
            Headroom = r.ReadInt16();
            Unk1 = r.ReadUInt16();
            LoopBegin = r.ReadUInt16();
            LoopEnd = r.ReadUInt16();
            PlayEnd = r.ReadUInt16();
            PlayBegin = r.ReadUInt16();
            Unk2 = r.ReadInt32();
            Peak = r.ReadUInt16();
            Codec = (AwcCodecType)r.ReadInt16();
            VorbisHeaderLength = r.ReadUInt16();

            if (VorbisHeaderLength > 0)
            {
                VorbisChunk = new AwcVorbisChunk(new AwcChunkInfo() { Type = AwcChunkType.vorbisheader });
                VorbisChunk.Read(r);
            }
        }

        public override void Write(DataWriter w)
        {
            w.Write(Samples);
            w.Write(LoopPoint);
            w.Write(SamplesPerSecond);
            w.Write(Headroom);
            w.Write(Unk1);
            w.Write(LoopBegin);
            w.Write(LoopEnd);
            w.Write(PlayEnd);
            w.Write(PlayBegin);
            w.Write(Unk2);
            w.Write(Peak.Value);
            w.Write((short)Codec);
            w.Write(VorbisHeaderLength);

            if (VorbisHeaderLength > 0 && VorbisChunk != null)
            {
                VorbisChunk.Write(w);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.StringTag(sb, indent, "Codec", Codec.ToString());
            AwcXml.ValueTag(sb, indent, "Samples", Samples.ToString());
            AwcXml.ValueTag(sb, indent, "SampleRate", SamplesPerSecond.ToString());
            AwcXml.ValueTag(sb, indent, "Headroom", Headroom.ToString());
            AwcXml.ValueTag(sb, indent, "PlayBegin", PlayBegin.ToString());
            AwcXml.ValueTag(sb, indent, "PlayEnd", PlayEnd.ToString());
            AwcXml.ValueTag(sb, indent, "LoopBegin", LoopBegin.ToString());
            AwcXml.ValueTag(sb, indent, "LoopEnd", LoopEnd.ToString());
            AwcXml.ValueTag(sb, indent, "LoopPoint", LoopPoint.ToString());
            AwcXml.ValueTag(sb, indent, "VorbisHeaderLength", VorbisHeaderLength.ToString());
            AwcXml.ValueTag(sb, indent, "Unk1", Unk1.ToString());
            AwcXml.ValueTag(sb, indent, "Unk2", Unk2.ToString());
            AwcXml.ValueTag(sb, indent, "Unk3", Peak.ToString());
        }

        public override void ReadXml(XmlNode node)
        {
            Codec = Xml.GetChildEnumInnerText<AwcCodecType>(node, "Codec");
            Samples = Xml.GetChildUIntAttribute(node, "Samples");
            SamplesPerSecond = (ushort)Xml.GetChildUIntAttribute(node, "SampleRate");
            Headroom = (short)Xml.GetChildIntAttribute(node, "Headroom");
            PlayBegin = (byte)Xml.GetChildUIntAttribute(node, "PlayBegin");
            PlayEnd = (ushort)Xml.GetChildUIntAttribute(node, "PlayEnd");
            LoopBegin = (ushort)Xml.GetChildUIntAttribute(node, "LoopBegin");
            LoopEnd = (ushort)Xml.GetChildUIntAttribute(node, "LoopEnd");
            LoopPoint = Xml.GetChildIntAttribute(node, "LoopPoint");
            VorbisHeaderLength = (ushort)Xml.GetChildUIntAttribute(node, "VorbisHeaderLength");
            Unk1 = (ushort)Xml.GetChildUIntAttribute(node, "Unk1");
            Unk2 = Xml.GetChildIntAttribute(node, "Unk2");
            Peak = (ushort)Xml.GetChildUIntAttribute(node, "Unk3");
        }

        public override string ToString()
        {
            return "format: " + Codec.ToString() + ": " + Samples.ToString() + " samples, " + SamplesPerSecond.ToString() + " samples/sec, headroom: " + Headroom.ToString();
        }
    }

    [TC(typeof(EXP))]
    public class AwcStreamFormatChunk : AwcChunk
    {
        public override int ChunkSize => 12 + (Channels?.Length ?? 0) * 16;
        public uint BlockCount { get; set; }
        public uint BlockSize { get; set; }
        public uint ChannelCount { get; set; }
        public AwcStreamFormat[] Channels { get; set; }

        public AwcStreamFormatChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            BlockCount = r.ReadUInt32();
            BlockSize = r.ReadUInt32();
            ChannelCount = r.ReadUInt32();

            var channels = new List<AwcStreamFormat>();
            for (int i = 0; i < ChannelCount; i++)
            {
                var itemInfo = new AwcStreamFormat();
                itemInfo.Read(r);
                channels.Add(itemInfo);
            }
            Channels = channels.ToArray();
        }

        public override void Write(DataWriter w)
        {
            ChannelCount = (uint)(Channels?.Length ?? 0);
            w.Write(BlockCount);
            w.Write(BlockSize);
            w.Write(ChannelCount);

            for (int i = 0; i < ChannelCount; i++)
            {
                Channels[i].Write(w);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.ValueTag(sb, indent, "BlockSize", BlockSize.ToString());
            //this is mostly just a placeholder. in XML, channel format is written with each channel stream
        }

        public override void ReadXml(XmlNode node)
        {
            BlockSize = Xml.GetChildUIntAttribute(node, "BlockSize");
        }

        public override string ToString()
        {
            return "streamformat: " + ChannelCount.ToString() + " channels, " + BlockCount.ToString() + " blocks, " + BlockSize.ToString() + " bytes per block";
        }
    }

    [TC(typeof(EXP))]
    public class AwcStreamFormat
    {
        public uint Id { get; set; }
        public uint Samples { get; set; }
        public short Headroom { get; set; }
        public ushort SamplesPerSecond { get; set; }
        public AwcCodecType Codec { get; set; } = AwcCodecType.ADPCM;
        public byte Unused1 { get; set; }
        public ushort LoopBegin { get; set; }

        public void Read(DataReader r)
        {
            Id = r.ReadUInt32();
            Samples = r.ReadUInt32();
            Headroom = r.ReadInt16();
            SamplesPerSecond = r.ReadUInt16();
            Codec = (AwcCodecType)r.ReadByte();
            Unused1 = r.ReadByte();
            LoopBegin = r.ReadUInt16();
        }

        public void Write(DataWriter w)
        {
            w.Write(Id);
            w.Write(Samples);
            w.Write(Headroom);
            w.Write(SamplesPerSecond);
            w.Write((byte)Codec);
            w.Write(Unused1);
            w.Write(LoopBegin);
        }

        public void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Codec", Codec.ToString());
            AwcXml.ValueTag(sb, indent, "Samples", Samples.ToString());
            AwcXml.ValueTag(sb, indent, "SampleRate", SamplesPerSecond.ToString());
            AwcXml.ValueTag(sb, indent, "Headroom", Headroom.ToString());
        }

        public void ReadXml(XmlNode node)
        {
            Codec = Xml.GetChildEnumInnerText<AwcCodecType>(node, "Codec");
            Samples = Xml.GetChildUIntAttribute(node, "Samples");
            SamplesPerSecond = (ushort)Xml.GetChildUIntAttribute(node, "SampleRate");
            Headroom = (short)Xml.GetChildIntAttribute(node, "Headroom");
        }

        public override string ToString()
        {
            return Id.ToString() + ", " + Codec.ToString() + ": " + Samples.ToString() + " samples, " + SamplesPerSecond.ToString() + " samples/sec, headroom: " + Headroom.ToString();
        }
    }

    [TC(typeof(EXP))]
    public class AwcAnimationChunk : AwcChunk
    {
        public override int ChunkSize => Data?.Length ?? 0;

        public byte[] Data { get; set; }
        //public ClipDictionary ClipDict { get; set; }

        public AwcAnimationChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            Data = r.ReadBytes(ChunkInfo.Size);

            /*if ((Data == null) || (Data.Length < 16)) return;

            var data = Data;

            RpfResourceFileEntry resentry = new RpfResourceFileEntry();
            uint rsc7 = BitConverter.ToUInt32(data, 0);
            int version = BitConverter.ToInt32(data, 4);
            resentry.SystemFlags = BitConverter.ToUInt32(data, 8);
            resentry.GraphicsFlags = BitConverter.ToUInt32(data, 12);

            if (rsc7 != 0x37435352)
            { } //testing..
            if (version != 46) //46 is Clip Dictionary...
            { }

            int newlen = data.Length - 16; //trim the header from the data passed to the next step.
            int arrlen = Math.Max(newlen, resentry.SystemSize + resentry.GraphicsSize);//expand it as necessary for the reader.
            byte[] newdata = new byte[arrlen];
            Buffer.BlockCopy(data, 16, newdata, 0, newlen);
            data = newdata;

            ResourceDataReader rd = new ResourceDataReader(resentry, data);

            ClipDict = rd.ReadBlock<ClipDictionary>();*/
        }

        public override void Write(DataWriter w)
        {
            w.Write(Data);
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            /*AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            if (ClipDict != null)
            {
                AwcXml.OpenTag(sb, indent, "ClipDictionary");
                ClipDict.WriteXml(sb, indent + 1);
                AwcXml.CloseTag(sb, indent, "ClipDictionary");
            }*/
        }

        public override void ReadXml(XmlNode node)
        {
            /*var dnode = node.SelectSingleNode("ClipDictionary");
            if (dnode != null)
            {
                ClipDict = new ClipDictionary();
                ClipDict.ReadXml(dnode);

                Data = ResourceBuilder.Build(ClipDict, 46, false); //ycd is 46...
            }*/
        }

        public override string ToString()
        {
            return "animation: "; //+ (ClipDict?.ClipsMapEntries ?? 0).ToString() + " entries";
        }
    }

    [TC(typeof(EXP))]
    public class AwcPeakChunk : AwcChunk
    {
        public override int ChunkSize => (Data?.Length ?? 0) * 2;

        public ushort[] Data { get; set; }

        public AwcPeakChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            var count = ChunkInfo.Size / 2;
            Data = new ushort[count];

            for (int i = 0; i < count; i++)
            {
                Data[i] = r.ReadUInt16();
            }
        }

        public override void Write(DataWriter w)
        {
            if (Data != null)
            {
                for (int i = 0; i < Data.Length; i++)
                {
                    w.Write(Data[i]);
                }
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            ////this is just a placeholder. in XML, peak data is generated from imported WAV data
            //AwcXml.WriteRawArray(sb, Data, indent, "Data", "");
        }

        public override void ReadXml(XmlNode node)
        {
            //Data = Xml.GetChildRawUshortArray(node, "Data");
        }

        public override string ToString()
        {
            if (Data == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Data.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Data[i].ToString());
            }
            return "peak: " + sb.ToString();
        }
    }

    [TC(typeof(EXP))]
    public class AwcGestureChunk : AwcChunk
    {
        public override int ChunkSize => (Gestures?.Length ?? 0) * 36;

        public Gesture[] Gestures { get; set; }

        public class Gesture : IMetaXmlItem
        {
            public MetaHash Name { get; set; }
            public MetaHash UnkHash { get; set; }
            public MetaHash UnkHash2 { get; set; }
            public uint UnkCount { get; set; }
            public uint UnkUint { get; set; }
            public uint UnkUint2 { get; set; }
            public uint UnkUint3 { get; set; }
            public uint UnkUint4 { get; set; }
            public uint UnkUint5 { get; set; }

            public void Read(DataReader r)
            {
                Name = r.ReadUInt32();
                UnkHash = r.ReadUInt32();
                UnkHash2 = r.ReadUInt32();
                UnkCount = r.ReadUInt32();
                UnkUint = r.ReadUInt32();
                UnkUint2 = r.ReadUInt32();
                UnkUint3 = r.ReadUInt32();
                UnkUint4 = r.ReadUInt32();
                UnkUint5 = r.ReadUInt32();

                //switch (Name)
                //{
                //    case 0xceda50be: //
                //    case 0x452c06fc: //
                //    case 0xba377ce2: //
                //    case 0xbe4c6d06: //
                //    case 0x9db051b4: //
                //    case 0x8a726e9f: //
                //    case 0x1f60ea95: //
                //    case 0x14e63a65: //
                //    case 0x32b4abf4: //
                //    case 0xe2b1dd62: //
                //    case 0x482d3572: //
                //    case 0x32f8d7a7: //
                //    case 0x9144296b: //
                //    case 0x3c73a8a4: //
                //    case 0x057c10c5: //
                //    case 0x981a4da0: //
                //    case 0x519d5f74: //
                //    case 0x0de43bc8: //
                //    case 0x89c16359: //
                //    case 0xd8884a6b: //
                //    case 0xfec7eb20: //
                //    case 0x06f0f709: //
                //    case 0x788a8abd: //
                //        break;
                //    default:
                //        break;//and more...
                //}

                //switch (UnkUint2)
                //{
                //    case 1:
                //    case 0:
                //        break;
                //    default:
                //        break;//no hit
                //}
            }

            public void Write(DataWriter w)
            {
                w.Write(Name);
                w.Write(UnkHash);
                w.Write(UnkHash2);
                w.Write(UnkCount);
                w.Write(UnkUint);
                w.Write(UnkUint2);
                w.Write(UnkUint3);
                w.Write(UnkUint4);
                w.Write(UnkUint5);
            }

            public void WriteXml(StringBuilder sb, int indent)
            {
                AwcXml.StringTag(sb, indent, "Name", AwcXml.HashString(Name));
                AwcXml.StringTag(sb, indent, "UnkHash", AwcXml.HashString(UnkHash));
                AwcXml.StringTag(sb, indent, "UnkHash2", AwcXml.HashString(UnkHash2));
                AwcXml.ValueTag(sb, indent, "UnkCount", UnkCount.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUint1", UnkCount.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUint2", UnkUint2.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUint3", UnkUint3.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUint4", UnkUint4.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUint5", UnkUint5.ToString());
            }

            public void ReadXml(XmlNode node)
            {
                Name = XmlMeta.GetHash(Xml.GetChildInnerText(node, "Name"));
                UnkHash = XmlMeta.GetHash(Xml.GetChildInnerText(node, "UnkHash"));
                UnkHash2 = XmlMeta.GetHash(Xml.GetChildInnerText(node, "UnkHash2"));
                UnkCount = Xml.GetChildUIntAttribute(node, "UnkCount");
                UnkUint = Xml.GetChildUIntAttribute(node, "UnkUint");
                UnkUint2 = Xml.GetChildUIntAttribute(node, "UnkUint2");
                UnkUint3 = Xml.GetChildUIntAttribute(node, "UnkUint3");
                UnkUint4 = Xml.GetChildUIntAttribute(node, "UnkUint4");
                UnkUint5 = Xml.GetChildUIntAttribute(node, "UnkUint5");
            }

            public override string ToString()
            {
                return Name.ToString(); //+ ": " + UnkUint1.ToString() + ", " + UnkFloat1.ToString() + ", " + UnkFloat2.ToString() + ", " + UnkFloat3.ToString() + ", " + UnkFloat4.ToString() + ", " + UnkFloat5.ToString() + ", " + UnkFloat6.ToString() + ", " + UnkUint2.ToString();
            }
        }

        public AwcGestureChunk(AwcChunkInfo info) : base(info)
        { }

        public override void Read(DataReader r)
        {
            // (hash, uint, 6x floats, uint) * n

            //if ((ChunkInfo.Size % 36) != 0)
            //{ }//no hit
            var count = ChunkInfo.Size / 36;
            Gestures = new Gesture[count];
            for (int i = 0; i < count; i++)
            {
                var g = new Gesture();
                g.Read(r);
                Gestures[i] = g;
            }
        }

        public override void Write(DataWriter w)
        {
            for (int i = 0; i < (Gestures?.Length ?? 0); i++)
            {
                Gestures[i].Write(w);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.WriteItemArray(sb, Gestures, indent, "Gestures");
        }

        public override void ReadXml(XmlNode node)
        {
            Gestures = XmlMeta.ReadItemArray<Gesture>(node, "Gestures");
        }

        public override string ToString()
        {
            return "gesture: " + (Gestures?.Length ?? 0).ToString() + " items";
        }
    }

    [TC(typeof(EXP))]
    public class AwcGranularGrainsChunk : AwcChunk
    {
        public override int ChunkSize => 4 + (GranularGrains?.Length ?? 0) * 12;

        public GranularGrain[] GranularGrains { get; set; }
        public float UnkFloat1 { get; set; }

        public class GranularGrain : IMetaXmlItem
        {
            public uint UnkUint1 { get; set; } //sample offset?
            public float UnkFloat1 { get; set; } // duration..? rpm..?
            public ushort UnkUshort1 { get; set; } //peak low?
            public ushort UnkUshort2 { get; set; } //peak high?

            public void Read(DataReader r)
            {
                UnkUint1 = r.ReadUInt32();
                UnkFloat1 = r.ReadSingle();
                UnkUshort1 = r.ReadUInt16();
                UnkUshort2 = r.ReadUInt16();
            }

            public void Write(DataWriter w)
            {
                w.Write(UnkUint1);
                w.Write(UnkFloat1);
                w.Write(UnkUshort1);
                w.Write(UnkUshort2);
            }

            public void WriteLine(StringBuilder sb)
            {
                sb.Append(UnkUint1.ToString());
                sb.Append(" ");
                sb.Append(FloatUtil.ToString(UnkFloat1));
                sb.Append(" ");
                sb.Append(UnkUshort1.ToString());
                sb.Append(" ");
                sb.Append(UnkUshort2.ToString());
                sb.AppendLine();
            }

            public void ReadLine(string s)
            {
                var split = s.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                var list = new List<string>();
                foreach (var str in split)
                {
                    var tstr = str.Trim();
                    if (!string.IsNullOrEmpty(tstr))
                    {
                        list.Add(tstr);
                    }
                }
                if (list.Count >= 4)
                {
                    uint.TryParse(list[0], out uint u1);
                    FloatUtil.TryParse(list[1], out float f1);
                    ushort.TryParse(list[2], out ushort s1);
                    ushort.TryParse(list[3], out ushort s2);
                    UnkUint1 = u1;
                    UnkFloat1 = f1;
                    UnkUshort1 = s1;
                    UnkUshort2 = s2;
                }
            }

            public void ReadXml(XmlNode node)
            {
                UnkUint1 = Xml.GetChildUIntAttribute(node, "UnkUint1");
                UnkFloat1 = Xml.GetChildFloatAttribute(node, "UnkFloat1");
                UnkUshort1 = (ushort)Xml.GetChildUIntAttribute(node, "UnkUshort1");
                UnkUshort2 = (ushort)Xml.GetChildUIntAttribute(node, "UnkUshort2");
            }

            public void WriteXml(StringBuilder sb, int indent)
            {
                AwcXml.ValueTag(sb, indent, "UnkUint1", UnkUint1.ToString());
                AwcXml.ValueTag(sb, indent, "UnkFloat1", FloatUtil.ToString(UnkFloat1));
                AwcXml.ValueTag(sb, indent, "UnkUshort1", UnkUshort1.ToString());
                AwcXml.ValueTag(sb, indent, "UnkUshort2", UnkUshort2.ToString());
            }

            public override string ToString()
            {
                return UnkUint1.ToString() + ", " + UnkFloat1.ToString() + ", " + UnkUshort1.ToString() + ", " + UnkUshort2.ToString();
            }
        }

        public AwcGranularGrainsChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            //int, (2x floats, int) * n ?

            //if ((ChunkInfo.Size % 12) != 4)
            //{ }//no hit
            var count = (ChunkInfo.Size - 4) / 12;
            GranularGrains = new GranularGrain[count];
            for (int i = 0; i < count; i++)
            {
                var g = new GranularGrain();
                g.Read(r);
                GranularGrains[i] = g;
            }
            UnkFloat1 = r.ReadSingle();

            //if (UnkFloat1 > 1.0f)
            //{ }//no hit
            //if (UnkFloat1 < 0.45833f)
            //{ }//no hit
        }

        public override void Write(DataWriter w)
        {
            for (int i = 0; i < (GranularGrains?.Length ?? 0); i++)
            {
                GranularGrains[i].Write(w);
            }
            w.Write(UnkFloat1);
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.ValueTag(sb, indent, "UnkFloat1", FloatUtil.ToString(UnkFloat1));
            //AwcXml.WriteCustomItemArray(sb, GranularGrains, indent, "GranularGrains");
            if (GranularGrains != null)
            {
                AwcXml.OpenTag(sb, indent, "GranularGrains");
                var cind = indent + 1;
                foreach (var grain in GranularGrains)
                {
                    AwcXml.Indent(sb, cind);
                    grain.WriteLine(sb);
                }
                AwcXml.CloseTag(sb, indent, "GranularGrains");
            }
        }

        public override void ReadXml(XmlNode node)
        {
            UnkFloat1 = Xml.GetChildFloatAttribute(node, "UnkFloat1");
            //GranularGrains = XmlMeta.ReadItemArray<GranularGrain>(node, "GranularGrains");
            var ggnode = node.SelectSingleNode("GranularGrains");
            if (ggnode != null)
            {
                var gglist = new List<GranularGrain>();
                var ggstr = ggnode.InnerText.Trim();
                var ggstrs = ggstr.Split('\n');
                foreach (var ggrstr in ggstrs)
                {
                    var rstr = ggrstr.Trim();
                    var ggr = new GranularGrain();
                    ggr.ReadLine(rstr);
                    gglist.Add(ggr);
                }
                GranularGrains = gglist.ToArray();
            }
        }

        public override string ToString()
        {
            return "granulargrains: " + (GranularGrains?.Length ?? 0).ToString() + " items";
        }
    }

    [TC(typeof(EXP))]
    public class AwcGranularLoopsChunk : AwcChunk
    {
        public override int ChunkSize
        {
            get
            {
                int size = 4 + (GranularLoops?.Length ?? 0) * 12;
                if (GranularLoops != null)
                {
                    foreach (var loop in GranularLoops)
                    {
                        size += (loop?.Grains?.Length ?? 0) * 4;
                    }
                }
                return size;
            }
        }

        public uint GranularLoopsCount { get; set; }
        public GranularLoop[] GranularLoops { get; set; }

        public class GranularLoop : IMetaXmlItem
        {
            public uint UnkUint1 { get; set; } = 2; //style="walk"?
            public uint GrainCount { get; set; }
            public MetaHash Identifier { get; set; } = 0x4c633d07; // "loop"
            public uint[] Grains { get; set; }

            public void Read(DataReader r)
            {
                UnkUint1 = r.ReadUInt32();
                GrainCount = r.ReadUInt32();
                Identifier = r.ReadUInt32();
                Grains = new uint[GrainCount];
                for (int i = 0; i < GrainCount; i++)
                {
                    Grains[i] = r.ReadUInt32();
                }

                //switch (UnkUint1)
                //{
                //    case 2:
                //        break;
                //    default:
                //        break;//no hit
                //}
                //switch (Hash)
                //{
                //    case 0x4c633d07:
                //        break;
                //    default:
                //        break;//no hit
                //}
            }

            public void Write(DataWriter w)
            {
                GrainCount = (uint)(Grains?.Length ?? 0);
                w.Write(UnkUint1);
                w.Write(GrainCount);
                w.Write(Identifier);
                for (int i = 0; i < GrainCount; i++)
                {
                    w.Write(Grains[i]);
                }
            }

            public void WriteXml(StringBuilder sb, int indent)
            {
                //AwcXml.ValueTag(sb, indent, "UnkUint1", UnkUint1.ToString());
                //AwcXml.StringTag(sb, indent, "Identifier", AwcXml.HashString(Hash));
                AwcXml.WriteRawArray(sb, Grains, indent, "Grains", "");
            }

            public void ReadXml(XmlNode node)
            {
                //UnkUint1 = Xml.GetChildUIntAttribute(node, "UnkUint1");
                //Hash = XmlMeta.GetHash(Xml.GetChildInnerText(node, "Identifier"));
                Grains = Xml.GetChildRawUintArray(node, "Grains");
            }

            public override string ToString()
            {
                return Identifier.ToString() + ": " + UnkUint1.ToString() + ": " + GrainCount.ToString() + " items";
            }
        }

        public AwcGranularLoopsChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            //uint count
            // [count*items]: uint(type?), uint(count2), hash, [count2*uint]

            GranularLoopsCount = r.ReadUInt32();
            GranularLoops = new GranularLoop[GranularLoopsCount];
            for (int i = 0; i < GranularLoopsCount; i++)
            {
                var g = new GranularLoop();
                g.Read(r);
                GranularLoops[i] = g;
            }
        }

        public override void Write(DataWriter w)
        {
            GranularLoopsCount = (uint)(GranularLoops?.Length ?? 0);
            w.Write(GranularLoopsCount);
            for (int i = 0; i < GranularLoopsCount; i++)
            {
                GranularLoops[i].Write(w);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.WriteItemArray(sb, GranularLoops, indent, "GranularLoops");
        }

        public override void ReadXml(XmlNode node)
        {
            GranularLoops = XmlMeta.ReadItemArray<GranularLoop>(node, "GranularLoops");
        }

        public override string ToString()
        {
            return "granularloops: " + (GranularLoops?.Length ?? 0).ToString() + " items";
        }
    }

    [TC(typeof(EXP))]
    public class AwcMarkersChunk : AwcChunk
    {
        public override int ChunkSize => (Markers?.Length ?? 0) * 16;

        public Marker[] Markers { get; set; }

        public class Marker : IMetaXmlItem
        {
            public MetaHash Name { get; set; }
            public MetaHash Value { get; set; }//usually a float, but in some cases a hash, or other value
            public uint SampleOffset { get; set; }
            public uint Unused { get; set; }

            public void Read(DataReader r)
            {
                Name = r.ReadUInt32();
                Value = r.ReadUInt32();
                SampleOffset = r.ReadUInt32();
                Unused = r.ReadUInt32();

                //switch (Name)
                //{
                //    case 0:
                //    case 0xa6d93246: // trackid
                //    case 0xe89ae78c: // beat
                //    case 0xf31b4f6a: // rockout
                //    case 0x08dba0f8: // dj
                //    case 0x7a495db3: // tempo
                //    case 0x14d857be: // g_s
                //        break;
                //    case 0xcd171e55: //
                //    case 0x806b80c9: // 1
                //    case 0x91aa2346: // 2
                //    case 0x11976678: // r_p
                //    case 0x91be54cb: //
                //    case 0xab2238c0: //
                //    case 0xdb599288: //
                //    case 0x2ce40eb5: //
                //    case 0xa35e1092: // 01
                //    case 0x1332b405: // tank_jump
                //    case 0x2b20b891: //
                //    case 0x8aa726e7: // tank_jump_land
                //    case 0xe0bfba99: // tank_turret_move
                //    case 0x1d91339e: //
                //    case 0xa5344b07: //
                //    case 0x7a7cba39: // tank_weapon_main_cannon_hit
                //    case 0xd66a90c3: //
                //    case 0x1fd18857: // 14
                //    case 0x65a52c67: //
                //    case 0xd8846402: // uihit
                //    case 0x8958bce4: // m_p
                //        if (Value != 0)
                //        { }//no hit
                //        break;
                //    default:
                //        break;//no hit
                //}

                //if (Unused != 0)
                //{ }//no hit
            }

            public void Write(DataWriter w)
            {
                w.Write(Name);
                w.Write(Value);
                w.Write(SampleOffset);
                w.Write(Unused);
            }

            public void WriteXml(StringBuilder sb, int indent)
            {
                AwcXml.StringTag(sb, indent, "Name", AwcXml.HashString(Name));
                switch (Name)
                {
                    case 0xf31b4f6a: // rockout
                    case 0x08dba0f8: // dj
                    case 0x14d857be: // g_s
                        AwcXml.StringTag(sb, indent, "Value", AwcXml.HashString(Value));
                        break;

                    default:
                        AwcXml.ValueTag(sb, indent, "Value", FloatUtil.ToString(Value.Float));
                        break;
                }
                AwcXml.ValueTag(sb, indent, "SampleOffset", SampleOffset.ToString());
            }

            public void ReadXml(XmlNode node)
            {
                Name = XmlMeta.GetHash(Xml.GetChildInnerText(node, "Name"));
                switch (Name)
                {
                    case 0xf31b4f6a: // rockout
                    case 0x08dba0f8: // dj
                    case 0x14d857be: // g_s
                        Value = XmlMeta.GetHash(Xml.GetChildInnerText(node, "Value"));
                        break;

                    default:
                        var f = Xml.GetChildFloatAttribute(node, "Value");
                        Value = BitConverter.ToUInt32(BitConverter.GetBytes(f));
                        break;
                }
                SampleOffset = Xml.GetChildUIntAttribute(node, "SampleOffset");
            }

            public override string ToString()
            {
                var valstr = Value.Float.ToString();
                switch (Name)
                {
                    case 0xf31b4f6a: // rockout
                    case 0x08dba0f8: // dj
                    case 0x14d857be: // g_s
                        valstr = Value.ToString();
                        break;
                }

                return Name.ToString() + ": " + valstr + ", " + SampleOffset.ToString() + ", " + Unused.ToString();
            }
        }

        public AwcMarkersChunk(AwcChunkInfo info) : base(info)
        { }

        public override void Read(DataReader r)
        {
            //if ((ChunkInfo.Size % 16) != 0)
            //{ }//no hit
            var count = ChunkInfo.Size / 16;
            Markers = new Marker[count];
            for (int i = 0; i < count; i++)
            {
                var m = new Marker();
                m.Read(r);
                Markers[i] = m;
            }
        }

        public override void Write(DataWriter w)
        {
            for (int i = 0; i < (Markers?.Length ?? 0); i++)
            {
                Markers[i].Write(w);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            AwcXml.WriteItemArray(sb, Markers, indent, "Markers");
        }

        public override void ReadXml(XmlNode node)
        {
            Markers = XmlMeta.ReadItemArray<Marker>(node, "Markers");
        }

        public override string ToString()
        {
            return "markers: " + (Markers?.Length ?? 0).ToString() + " markers";
        }
    }

    [TC(typeof(EXP))]
    public class AwcMIDIChunk : AwcChunk
    {
        public override int ChunkSize => Data?.Length ?? 0;

        public byte[] Data { get; set; }

        public AwcMIDIChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            Data = r.ReadBytes(ChunkInfo.Size);
        }

        public override void Write(DataWriter w)
        {
            w.Write(Data);
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            //this is just a placeholder, as midi data will be written as a midi file
        }

        public override void ReadXml(XmlNode node)
        {
        }

        public override string ToString()
        {
            return "mid: " + (Data?.Length ?? 0).ToString() + " bytes";
        }
    }

    [TC(typeof(EXP))]
    public class AwcSeekTableChunk : AwcChunk
    {
        public override int ChunkSize => (SeekTable?.Length ?? 0) * 4;

        public uint[] SeekTable { get; set; }

        public AwcSeekTableChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            var count = ChunkInfo.Size / 4;
            SeekTable = new uint[count];
            for (int i = 0; i < count; i++)
            {
                SeekTable[i] = r.ReadUInt32();
            }
        }

        public override void Write(DataWriter w)
        {
            for (int i = 0; i < (SeekTable?.Length ?? 0); i++)
            {
                w.Write(SeekTable[i]);
            }
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            //this is just a placeholder, since the seek table will be built dynamically by CW.
        }

        public override void ReadXml(XmlNode node)
        {
        }

        public override string ToString()
        {
            return "seektable: " + (SeekTable?.Length ?? 0).ToString() + " items";
        }
    }

    [TC(typeof(EXP))]
    public class AwcVorbisChunk : AwcChunk
    {
        public override int ChunkSize => 12 + DataSection1.Length + DataSection2.Length + DataSection3.Length;
        public byte[] DataSection1 { get; set; }
        public byte[] DataSection2 { get; set; }
        public byte[] DataSection3 { get; set; }

        public AwcVorbisChunk(AwcChunkInfo info) : base(info)
        {
        }

        public override void Read(DataReader r)
        {
            var sectionsize = r.ReadInt32();
            DataSection1 = r.ReadBytes(sectionsize);
            var sectionsize2 = r.ReadInt32();
            DataSection2 = r.ReadBytes(sectionsize2);
            var sectionsize3 = r.ReadInt32();
            DataSection3 = r.ReadBytes(sectionsize3);
        }

        public override void Write(DataWriter w)
        {
            w.Write(DataSection1.Length);
            w.Write(DataSection1);
            w.Write(DataSection2.Length);
            w.Write(DataSection2);
            w.Write(DataSection3.Length);
            w.Write(DataSection3);
        }

        public override void WriteXml(StringBuilder sb, int indent)
        {
            AwcXml.StringTag(sb, indent, "Type", ChunkInfo?.Type.ToString());
            //Placeholder, as vorbis data will be written to .ogg file with sound data.
        }

        public override void ReadXml(XmlNode node)
        {
        }

        public override string ToString()
        {
            return "vorbis: " + (ChunkInfo?.Size ?? 0).ToString() + " bytes";
        }
    }

    [TC(typeof(EXP))]
    public class AwcStreamDataBlock
    {
        public int DataLength { get; set; }//just for convenience
        public int SampleOffset { get; set; }//just for convenience
        public uint ChannelCount { get; set; }//just for convenience
        public AwcStreamFormatChunk ChannelInfo { get; set; } //just for convenience
        public AwcStreamDataChannel[] Channels { get; set; }

        public AwcStreamDataBlock()
        { }

        public AwcStreamDataBlock(byte[] data, AwcStreamFormatChunk channelInfo, Endianess endianess, int sampleOffset)
        {
            DataLength = data?.Length ?? 0;
            SampleOffset = sampleOffset;
            ChannelCount = channelInfo?.ChannelCount ?? 0;
            ChannelInfo = channelInfo;

            using (var ms = new MemoryStream(data))
            {
                var r = new DataReader(ms, endianess);
                Read(r);
            }
        }

        public void Read(DataReader r)
        {
            var ilist = new List<AwcStreamDataChannel>();
            for (int i = 0; i < ChannelCount; i++)
            {
                var channel = new AwcStreamDataChannel(ChannelInfo, i);
                channel.Read(r);
                ilist.Add(channel);
            }
            Channels = ilist.ToArray();

            foreach (var channel in Channels)
            {
                channel.ReadOffsets(r);
            }

            var padc = (0x800 - (r.Position % 0x800)) % 0x800;
            var padb = r.ReadBytes((int)padc);

            foreach (var channel in Channels)
            {
                channel.ReadData(r);
            }

            //if (r.Position != r.Length)
            //{ }//still more, just padding?
        }

        public void Write(DataWriter w)
        {
            foreach (var channel in Channels)
            {
                channel.Write(w);
            }
            foreach (var channel in Channels)
            {
                channel.WriteOffsets(w);
            }
            var padc = (0x800 - (w.Position % 0x800)) % 0x800;
            if (padc > 0)
            {
                w.Write(new byte[padc]);
            }
            foreach (var channel in Channels)
            {
                channel.WriteData(w);
            }
        }

        public override string ToString()
        {
            return DataLength.ToString() + " bytes";
        }
    }

    [TC(typeof(EXP))]
    public class AwcStreamDataChannel
    {
        public int StartBlock { get; set; }
        public int BlockCount { get; set; }
        public int Unused1 { get; set; }
        public int SampleCount { get; set; }
        public int Unused2 { get; set; }
        public int Unused3 { get; set; }

        public int[] SampleOffsets { get; set; }
        public byte[] Data { get; set; }

        public bool IsVorbis { get; set; } = false;

        public AwcStreamDataChannel(AwcStreamFormatChunk info, int channel = 0)
        {
            if (info.Channels[channel].Codec == AwcCodecType.VORBIS)
            {
                IsVorbis = true;
            }
        }

        public void Read(DataReader r)
        {
            StartBlock = r.ReadInt32();
            BlockCount = r.ReadInt32();
            Unused1 = r.ReadInt32();
            SampleCount = r.ReadInt32();

            if (IsVorbis)
            {
                Unused2 = r.ReadInt32();
                Unused3 = r.ReadInt32();
            }

            //if (Unused1 != 0)
            //{ }//no hit
            //if (Unused2 != 0)
            //{ }//no hit
            //if (Unused3 != 0)
            //{ }//no hit
        }

        public void Write(DataWriter w)
        {
            w.Write(StartBlock);
            w.Write(BlockCount);
            w.Write(Unused1);
            w.Write(SampleCount);

            if (IsVorbis)
            {
                w.Write(Unused2);
                w.Write(Unused3);
            }
        }

        public void ReadOffsets(DataReader r)
        {
            var olist = new List<int>();
            for (int i = 0; i < BlockCount; i++)
            {
                var v = r.ReadInt32();
                olist.Add(v);
            }
            SampleOffsets = olist.ToArray();
        }

        public void WriteOffsets(DataWriter w)
        {
            var smpoc = SampleOffsets?.Length ?? 0;
            for (int i = 0; i < BlockCount; i++)
            {
                w.Write((i < smpoc) ? SampleOffsets[i] : 0);
            }
        }

        public void ReadData(DataReader r)
        {
            var bcnt = BlockCount * 2048;
            Data = r.ReadBytes(bcnt);
        }

        public void WriteData(DataWriter w)
        {
            w.Write(Data);
        }

        public override string ToString()
        {
            return StartBlock.ToString() + ": " + BlockCount.ToString() + ", " + Unused1.ToString() + ", " + SampleCount.ToString() + ", " + Unused2.ToString() + ", " + Unused3.ToString();
        }
    }

    public class AwcXml : MetaXmlBase
    {
        public static string GetXml(AwcFile awc, string outputFolder = "")
        {
            var sb = new StringBuilder();
            sb.AppendLine(XmlHeader);

            if (awc != null)
            {
                AwcFile.WriteXmlNode(awc, sb, 0, outputFolder);
            }
            return sb.ToString();
        }
    }

    public class XmlAwc
    {
        public static AwcFile AwcYft(string xml, string inputFolder = "")
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return GetAwc(doc, inputFolder);
        }

        public static AwcFile GetAwc(XmlDocument doc, string inputFolder = "")
        {
            AwcFile r = null;
            var node = doc.DocumentElement;

            if (node != null)
            {
                r = AwcFile.ReadXmlNode(node, inputFolder);
                r.Name = Path.GetFileName(inputFolder);
            }
            return r;
        }
    }
}