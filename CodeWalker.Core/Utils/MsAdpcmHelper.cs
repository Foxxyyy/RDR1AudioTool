using System;
using System.Collections.Generic;
using System.IO;

namespace CodeWalker.Core.Utils
{
    public class MsAdpcmHelper
    {
        private static readonly short[] adpcmCoef = new short[16];
        private static short adpcmHistory1_16 = 0;
        private static short adpcmHistory2_16 = 0;
        private static int adpcmScale = 0;

        public static readonly short[,] Coefs =
        {
            { 256, 0 },
            { 512, -256 },
            { 0, 0 },
            { 192, 64 },
            { 240, 0 },
            { 460, -208 },
            { 392, -232 }
        };

        public static readonly short[] AdaptationTable =
        {
            230, 230, 230, 230,
            307, 409, 512, 614,
            768, 614, 512, 409,
            307, 230, 230, 230
        };

        private static int Clamp16(int val)
        {
            if (val > 32767) return 32767;
            else if (val < -32768) return -32768;
            else return val;
        }

        private static void WriteShortToByteArray(byte[] buffer, int index, short value)
        {
            buffer[index] = (byte)(value & 0xFF);
            buffer[index + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static short MSADPCMExpandNibbleShr(byte currentByte, int shift)
        {
            int code = (currentByte >> shift) & 0x0F;
            if ((code & 0x08) != 0) code -= 16;

            int predicted = adpcmHistory1_16 * adpcmCoef[0] + adpcmHistory2_16 * adpcmCoef[1];
            predicted >>= 8;
            predicted += code * adpcmScale;
            predicted = Clamp16(predicted);

            adpcmHistory2_16 = adpcmHistory1_16;
            adpcmHistory1_16 = (short)predicted;

            adpcmScale = (AdaptationTable[code & 0x0F] * adpcmScale) >> 8;
            if (adpcmScale < 16) adpcmScale = 16;

            return (short)predicted;
        }

        private static short MSADPCMExpandNibbleDiv(byte currentByte, int shift)
        {
            int code = (currentByte >> shift) & 0x0F;
            if ((code & 0x08) != 0) code -= 16;

            int predicted = adpcmHistory1_16 * adpcmCoef[0] + adpcmHistory2_16 * adpcmCoef[1];
            predicted /= 256;
            predicted += code * adpcmScale;
            predicted = Clamp16(predicted);

            adpcmHistory2_16 = adpcmHistory1_16;
            adpcmHistory1_16 = (short)predicted;

            adpcmScale = (AdaptationTable[code & 0x0F] * adpcmScale) / 256;
            if (adpcmScale < 16) adpcmScale = 16;

            return (short)predicted;
        }

        public static byte[] DecodeMSADPCMMono(byte[] data, int channelSpacing, int firstSample, int samplesToDo, int bytesPerFrame)
        {
            var br = new BinaryReader(new MemoryStream(data));
            int framesIn;
            int samplesPerFrame = (bytesPerFrame - 7) * 2 + 2;
            bool isShr = true;

            framesIn = firstSample / samplesPerFrame;
            br.BaseStream.Position = framesIn * bytesPerFrame;
            firstSample %= samplesPerFrame;

            int index = framesIn * bytesPerFrame;
            if (index >= data.Length)
            {
                index = (framesIn - 1) * bytesPerFrame;
            }

            if (firstSample == 0)
            {
                int coefIndex = data[index] & 0x07;
                adpcmCoef[0] = Coefs[coefIndex, 0];
                adpcmCoef[1] = Coefs[coefIndex, 1];
                adpcmScale = BitConverter.ToInt16(data, index + 1);
                adpcmHistory1_16 = BitConverter.ToInt16(data, index + 3);
                adpcmHistory2_16 = BitConverter.ToInt16(data, index + 5);
            }

            byte[] outBuffer = new byte[samplesToDo * 2 * channelSpacing];
            int outIndex = 0;

            if (firstSample == 0 && samplesToDo > 0)
            {
                WriteShortToByteArray(outBuffer, outIndex, adpcmHistory2_16);
                outIndex += channelSpacing * 2;
                firstSample++;
                samplesToDo--;
            }

            if (firstSample == 1 && samplesToDo > 0)
            {
                WriteShortToByteArray(outBuffer, outIndex, adpcmHistory1_16);
                outIndex += channelSpacing * 2;
                firstSample++;
                samplesToDo--;
            }

            for (int i = firstSample; i < firstSample + samplesToDo; i++)
            {
                byte currentByte = data[index + 7 + (i - 2) / 2];
                int shift = (i % 2 == 0) ? 4 : 0;
                short decodedSample = isShr ? MSADPCMExpandNibbleShr(currentByte, shift) : MSADPCMExpandNibbleDiv(currentByte, shift);

                WriteShortToByteArray(outBuffer, outIndex, decodedSample);
                outIndex += channelSpacing * 2;
            }
            return outBuffer;
        }

        public static byte[] Decode(byte[] data, int sampleCount)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            //Read MSADPCM header (first 0x4A bytes contain fmt info)
            var header = br.ReadBytes(0x4A);
            var brHeader = new BinaryReader(new MemoryStream(header));

            brHeader.BaseStream.Position = 0x18;
            var samplesHeader = brHeader.ReadInt32();

            brHeader.BaseStream.Position = 0x20;
            var blockSize = brHeader.ReadInt16();

            //Next comes audio data size and then actual samples
            var audioSize = br.ReadInt32();
            var block_data = br.ReadBytes(audioSize);

            var samplesPerBlock = (blockSize - 7) * 2 + 2;
            var samplesFilled = 0;
            var decodedAudio = new List<byte>();

            while (samplesFilled < sampleCount)
            {
                var samplesToDo = Math.Min(samplesPerBlock, sampleCount - samplesFilled);
                var chunkData = DecodeMSADPCMMono(block_data, 1, samplesFilled, samplesToDo, blockSize);

                decodedAudio.AddRange(chunkData);
                samplesFilled += samplesToDo;
            }
            return decodedAudio.ToArray();
        }

        private static byte[] EncodeBlock(short[] samples, int offset, int count, int blockSize) //Encodes a single ADPCM block from PCM16
        {
            //Pick best predictor
            var bestPredictor = 0;
            var bestError = long.MaxValue;

            for (int i = 0; i < 7; i++)
            {
                int c1 = Coefs[i, 0];
                int c2 = Coefs[i, 1];
                var predicted = (samples[offset + 1] * c1 + samples[offset] * c2) >> 8;
                var diff = samples[offset + 1] - predicted;
                long err = Math.Abs(diff);

                if (err < bestError)
                {
                    bestError = err;
                    bestPredictor = i;
                }
            }

            var predictor = bestPredictor;
            int coef1 = Coefs[predictor, 0];
            int coef2 = Coefs[predictor, 1];

            //Estimate samples
            var s2 = samples[offset];     //Older
            var s1 = samples[offset + 1]; //Newer

            //Initial delta (step size)
            var delta = (short)Math.Max(16, Math.Abs(s1 - s2));

            var buffer = new byte[blockSize];
            buffer[0] = (byte)predictor;
            BitConverter.GetBytes(delta).CopyTo(buffer, 1);
            BitConverter.GetBytes(s1).CopyTo(buffer, 3); //Sample 0
            BitConverter.GetBytes(s2).CopyTo(buffer, 5); //Sample 1

            int scale = delta;
            var bufIndex = 7;
            var outByte = 0;
            var highNibble = true;

            for (int i = 2; i < count; i++)
            {
                int pcm = samples[offset + i];
                var predicted = (s1 * coef1 + s2 * coef2) >> 8;

                //Brute-force search best nibble
                int bestNibble = 0;
                int bestErr = int.MaxValue;
                int bestRecon = 0;

                for (int cand = -8; cand <= 7; cand++)
                {
                    int recon = predicted + cand * scale;
                    recon = Clamp16(recon);
                    int err = Math.Abs(pcm - recon);

                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestNibble = cand;
                        bestRecon = recon;
                    }
                }

                int nibble = bestNibble;

                // Update history with best reconstruction
                s2 = s1;
                s1 = (short)bestRecon;

                // Update scale
                scale = (scale * AdaptationTable[nibble & 0x0F]) >> 8;
                if (scale < 16) scale = 16;

                // Pack nibble
                if (highNibble)
                {
                    outByte = (nibble & 0x0F) << 4;
                    highNibble = false;
                }
                else
                {
                    outByte |= (nibble & 0x0F);
                    buffer[bufIndex++] = (byte)outByte;
                    highNibble = true;
                }
            }

            if (!highNibble)
            {
                buffer[bufIndex++] = (byte)outByte;
            }
            return buffer;
        }

        public static byte[] EncodeMSADPCMMono(short[] pcmSamples, int blockSize) //Encodes a full PCM16 moo stream to MSADPCM
        {
            if (pcmSamples.Length < 2)
            {
                throw new ArgumentException("Needs at least 2 samples per block");
            }

            var samplesPerBlock = (blockSize - 7) * 2 + 2;
            var ms = new MemoryStream();
            var samplesDone = 0;

            while (samplesDone < pcmSamples.Length)
            {
                var cnt = Math.Min(samplesPerBlock, pcmSamples.Length - samplesDone);
                var block = EncodeBlock(pcmSamples, samplesDone, cnt, blockSize);
                ms.Write(block, 0, block.Length);
                samplesDone += cnt;
            }
            return ms.ToArray();
        }
    }
}