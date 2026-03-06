using CodeWalker.GameFiles;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.Compression;
using OggVorbisSharp;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RDR1AudioTool
{
    public partial class ReplaceAudioWindow : Window
    {
        public AwcCodecType CodecType = AwcCodecType.PCM;
        public byte[]? PcmData = null;
        public int SampleCount = 0;
        public int SampleRate = 0;
        public int InputChannels = 1;
        public bool StereoInput = false;

        public ReplaceAudioWindow(bool multiChannelFlag = false)
        {
            InitializeComponent();
            CodecSelectionBox.Items.Add("MSADPCM");

            if (!multiChannelFlag)
            {
                CodecSelectionBox.Items.Add("PCM");
            }

            CodecSelectionBox.Items.Add("ADPCM");
            CodecSelectionBox.Items.Add("Vorbis");
            CodecSelectionBox.Items.Add("OPUS");
            CodecSelectionBox.SelectedIndex = 0;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Audio Files|*.wav; *.mp3"
            };

            var result = dialog.ShowDialog();
            if (result == true)
            {
                FileTextBox.Text = dialog.FileName;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CodecSelectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var codec = (string)CodecSelectionBox.SelectedItem;
            switch (codec)
            {
                case "PCM":
                    CodecType = AwcCodecType.PCM;
                    break;
                case "ADPCM":
                    CodecType = AwcCodecType.ADPCM;
                    break;
                case "MSADPCM":
                    CodecType = AwcCodecType.MSADPCM;
                    break;
                case "Vorbis":
                    CodecType = AwcCodecType.VORBIS;
                    break;
                case "OPUS":
                    CodecType = AwcCodecType.OPUS;
                    break;
            }
        }

        private void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FileTextBox.Text))
            {
                MessageBox.Show("Please enter a path to the file.");
                return;
            }

            if (!Path.Exists(FileTextBox.Text))
            {
                MessageBox.Show("Invalid path.");
                return;
            }

            ReadFile(FileTextBox.Text);
            DialogResult = true;
            Close();
        }

        private void ReadFile(string fileName)
        {
            switch (Path.GetExtension(fileName))
            {
                case ".wav":
                    ReadWavFile(fileName);
                    break;
                case ".mp3":
                    ReadMp3File(fileName);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported file extension.");
            }
        }

        private static void ResampleWav(string inputPath, string outputPath, int newSampleRate = 24000)
        {
            using var reader = new WaveFileReader(inputPath);
            var outFormat = new WaveFormat(newSampleRate, reader.WaveFormat.Channels);
            using var resampler = new MediaFoundationResampler(reader, outFormat);
            resampler.ResamplerQuality = 60; // 1=lowest, 60=highest
            WaveFileWriter.CreateWaveFile(outputPath, resampler);
        }

        private void ReadWavFile(string fileName)
        {
            var wavFile = new WaveFileReader(fileName);
            var fmt = wavFile.WaveFormat;

            if (fmt.Encoding != WaveFormatEncoding.Pcm && fmt.Encoding != WaveFormatEncoding.Adpcm)
            {
                MessageBox.Show("Only PCM/ADPCM wav files are accepted.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ch = fmt.Channels;
            int maxCh = (CodecType == AwcCodecType.OPUS) ? 3 : 2;

            if (ch < 1 || ch > maxCh)
            {
                MessageBox.Show($"Wav file has {ch} channels. Max allowed for {CodecType} is {maxCh}.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PcmData = EncodeIfNeeded(wavFile);
            SampleRate = fmt.SampleRate;
            InputChannels = ch;
            StereoInput = fmt.Channels == 2;
            SampleCount = (PcmData != null) ? PcmData.Length / (2 * ch) : 0;
        }

        private void ReadMp3File(string fileName)
        {
            var mp3File = new Mp3FileReader(fileName);
            if (mp3File.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                MessageBox.Show("PCM wav files are only accepted.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (mp3File.WaveFormat.Channels > 2)
            {
                MessageBox.Show("Wav file has too many channels.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PcmData = EncodeIfNeeded(mp3File);
            SampleRate = mp3File.WaveFormat.SampleRate;

            if (mp3File.WaveFormat.Channels == 2)
            {
                StereoInput = true;
            }
            SampleCount = PcmData.Length / 2;
        }

        private byte[]? EncodeIfNeeded(IWaveProvider reader)
        {
            if (CodecType == AwcCodecType.ADPCM || CodecType == AwcCodecType.PCM || CodecType == AwcCodecType.MSADPCM)
            {
                var format = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
                var source = reader;
                
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm && reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                {
                    source = WaveFormatConversionStream.CreatePcmStream(reader as WaveStream);
                }

                var outputStream = new MemoryStream();
                var resampler = new MediaFoundationResampler(source, format);
                var array = new byte[resampler.WaveFormat.AverageBytesPerSecond * 4];

                while (true)
                {
                    int num = resampler.Read(array, 0, array.Length);
                    if (num == 0)
                    {
                        break;
                    }
                    outputStream.Write(array, 0, num);
                }

                resampler.Dispose();
                var bytes = outputStream.ToArray();
                outputStream.Dispose();
                return bytes;
            }
            else if (CodecType == AwcCodecType.VORBIS)
            {
                if (reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    var outputStream = new MemoryStream();
                    var array = new byte[reader.WaveFormat.AverageBytesPerSecond * 4];

                    while (true)
                    {
                        int num = reader.Read(array, 0, array.Length);
                        if (num == 0)
                        {
                            break;
                        }
                        outputStream.Write(array, 0, num);
                    }

                    var bytes = outputStream.ToArray();
                    outputStream.Dispose();
                    return bytes;
                }
                else 
                {
                    var format = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
                    using var resampler = new MediaFoundationResampler(reader, format);
                    using var ms = new MemoryStream();
                    
                    var array = new byte[format.AverageBytesPerSecond];
                    var num = 0;

                    while ((num = resampler.Read(array, 0, array.Length)) > 0)
                    {
                        ms.Write(array, 0, num);
                    }
                    return ms.ToArray();
                }
            }
            else if (CodecType == AwcCodecType.OPUS) //Correct?
            {
                var targetRate = 48000;
                var format = new WaveFormat(targetRate, 16, reader.WaveFormat.Channels);

                //Convert ADPCM to PCM if needed
                IWaveProvider source = reader;
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm && reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                {
                    source = WaveFormatConversionStream.CreatePcmStream(reader as WaveStream);
                }

                using var resampler = new MediaFoundationResampler(source, format);
                using var ms = new MemoryStream();
                
                var buffer = new byte[format.AverageBytesPerSecond]; //1sec chunks
                var read = 0;

                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }

                SampleRate = targetRate; //Update sample rate to match what we resampled to
                return ms.ToArray();
            }
            return null;
        }
    }
}
