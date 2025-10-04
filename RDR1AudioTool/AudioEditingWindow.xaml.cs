using CodeWalker;
using CodeWalker.GameFiles;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace RDR1AudioTool
{
    public partial class AudioEditingWindow : Window
    {
        private AwcFile? Awc = null;
        private WasapiOut? waveOut = null;
        private RawSourceWaveStream? sourceWaveStream = null;
        private readonly RoutedEventHandler? playHandler = null;
        private readonly RoutedEventHandler? pauseHandler = null;
        private readonly DispatcherTimer timer;
        private GridViewColumn lastSortedColumn = new();
        private readonly ObservableCollection<object> originalItems = new(); //Needed in order for search to work
        private ListSortDirection lastSortDirection = ListSortDirection.Ascending;
        private bool autoPlayEnabled = false;
        private bool autoPlayLoopEnabled = false;
        private int currentPlayingIndex = 0;
        private bool isPaused = false;

        public class ItemInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Length { get; set; }
            public string Size { get; set; }
            public AwcStream Stream { get; set; }

            public ItemInfo(AwcStream stream)
            {
                Name = stream.Name;
                Type = stream.TypeString;
                Length = stream.LengthStr;
                Size = TextUtil.GetBytesReadable(stream.ByteLength);
                Stream = stream;
            }
        }

        public class StereoItemInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Length { get; set; }
            public string Size { get; set; }
            public AwcStream StreamLeft { get; set; }
            public AwcStream StreamRight { get; set; }

            public StereoItemInfo(AwcStream streamLeft, AwcStream streamRight)
            {
                Name = streamLeft.Name[..^5];
                Type = streamLeft.TypeString;
                Length = streamLeft.LengthStr;
                Size = TextUtil.GetBytesReadable(streamLeft.ByteLength + streamRight.ByteLength);
                StreamLeft = streamLeft;
                StreamRight = streamRight;
            }
        }

        public AudioEditingWindow()
        {
            waveOut = new WasapiOut();
            InitializeComponent();

            timer = new DispatcherTimer(DispatcherPriority.Render); //Smoother slide
            timer.Tick += new EventHandler(Timer_Tick);
            timer.Interval = TimeSpan.FromMilliseconds(10); //Update the slider every 10 milliseconds so it has like a smooth slide

            playHandler = new RoutedEventHandler(PlayButton_Click);
            pauseHandler = new RoutedEventHandler(PauseButton_Click);

            VolumeResetButton.IsEnabled = false;
            VolumeSlider.IsEnabled = true;

            //Update volume (default is 15). The slider will dynamically change the Windows volume
            var deviceEnumerator = new MMDeviceEnumerator();
            var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            float systemVolume = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;

            VolumeSlider.Value = systemVolume * 100;
            VolumeLabel.Content = $"{VolumeSlider.Value:0}";
        }

        protected override void OnClosed(EventArgs e)
        {
            isPaused = false;
            Stop();
            base.OnClosed(e);
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not GridViewColumnHeader column || column.Tag is not string tag) return;
            if (lastSortedColumn != null && lastSortedColumn != column.Column)
            {
                lastSortedColumn.HeaderTemplate = null;
            }

            var direction = (lastSortedColumn == column.Column && lastSortDirection == ListSortDirection.Ascending) ? ListSortDirection.Descending : ListSortDirection.Ascending;
            column.Column.HeaderTemplate = direction == ListSortDirection.Ascending ? Resources["HeaderTemplateArrowUp"] as DataTemplate : Resources["HeaderTemplateArrowDown"] as DataTemplate;

            lastSortedColumn = column.Column;
            lastSortDirection = direction;

            SortListView(tag, direction);
        }

        private void SortListView(string tag, ListSortDirection direction)
        {
            var view = CollectionViewSource.GetDefaultView(StreamList.Items); //We use StreamList.Items because in RefreshList we set StreamList.ItemsSource to null so passing that in to this will do nothing!!
            if (view != null)
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(tag, direction));
                view.Refresh();
            }
        }

        private void OpenAWC_Click(object sender, RoutedEventArgs e)
        {
            var fbd = new OpenFileDialog
            {
                Filter = "Audio Wave Container (.awc)|*.awc"
            };

            if (fbd.ShowDialog() == true && !string.IsNullOrWhiteSpace(fbd.FileName))
            {
                ResetPlayer();
                ResetPlayerUI();

                using var stream = new FileStream(fbd.FileName, FileMode.Open);
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);

                Awc = new AwcFile();
                Awc.Load(memoryStream.ToArray(), Path.GetFileName(fbd.FileName));
                
                Title = $"RDR Audio Tool - {Path.GetFileName(fbd.FileName)}";
                originalItems.Clear();
                StreamList.Items.Clear();
                currentPlayingIndex = 0;
                isPaused = false;
                VolumeResetButton.IsEnabled = true;
                VolumeSlider.IsEnabled = true;

                RefreshList();
            }

            if (Awc == null)
            {
                SaveButton.IsEnabled = false;
                RenameButton.IsEnabled = false;
                ReplaceButton.IsEnabled = false;
                DeleteButton.IsEnabled = false;
                MoreOptionsButton.IsEnabled = false;
                slider.IsEnabled = false;
                PlayLastButton.IsEnabled = false;
                PlayButton.IsEnabled = false;
                PlayNextButton.IsEnabled = false;
                StreamList.IsEnabled = false;
            }
        }

        private void SaveAWC_Click(object sender, RoutedEventArgs e)
        {
            if (Awc == null) return;
            var dialog = new SaveFileDialog
            {
                FileName = Awc.Name
            };

            var dialogResult = dialog.ShowDialog();
            if (dialogResult == true)
            {
                if (Awc.MultiChannelFlag)
                {
                    Awc.MultiChannelSource?.CompactMultiChannelSources(Awc.Streams);
                }
                
                Awc.BuildPeakChunks();
                Awc.BuildChunkIndices();
                Awc.BuildStreamInfos();
                Awc.BuildStreamDict();
                File.WriteAllBytes(dialog.FileName, Awc.Save());
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (Awc.MultiChannelFlag)
            {
                string name = null;
                if (Awc.Streams[1].Name.EndsWith("_right"))
                {
                    name = Awc.Streams[1].Name.Substring(0, Awc.Streams[1].Name.Length - 6);
                }
                else
                {
                    name = "";
                }

                RenameWindow w = new RenameWindow(name);
                bool? r = w.ShowDialog();

                if (r == true)
                {
                    Awc.Streams[1].Name = w.String + "_right";
                    Awc.Streams[2].Name = w.String + "_left";
                }

                RefreshList();

                return;
            }

            RenameWindow window = new RenameWindow((StreamList.SelectedItem as ItemInfo).Stream.Name);

            bool? result = window.ShowDialog();

            if (result == true)
            {
                if (Awc?.Streams != null)
                {
                    for (int i = 0; i < Awc.Streams.Length; i++)
                    {
                        if (Awc.Streams[i].Name == (StreamList.SelectedItem as ItemInfo).Stream.Name)
                        {
                            Awc.Streams[i].Name = window.String;
                            RefreshList();
                            break;
                        }
                    }
                }
            }
        }

        private void RefreshList()
        {
            StreamList.ItemsSource = null;
            StreamList.Items.Clear();

            if (Awc?.Streams != null)
            {
                var hashStreams = new List<AwcStream>();
                var nameStreams = new List<AwcStream>();
                var filteredStreams = new List<AwcStream>();
                var strlist = Awc.Streams.ToList();

                foreach (var audio in strlist)
                {
                    if (audio.Name.StartsWith("0x"))
                        hashStreams.Add(audio);
                    else
                        nameStreams.Add(audio);
                }

                hashStreams.Sort((a, b) => a.Hash.Hash.CompareTo(b.Hash.Hash));
                nameStreams.Sort(new AlphanumericComparer());

                foreach (var audio in hashStreams)
                {
                    var stereo = (audio.ChannelStreams?.Length == 2);
                    if ((audio.StreamBlocks != null) && (!stereo)) continue;//don't display multichannel source audios
                    var name = audio.Name;
                    if (stereo) continue; // name = "(Stereo Playback)";

                    StreamList.Items.Add(new ItemInfo(audio));
                    originalItems.Add(new ItemInfo(audio));
                }

                foreach (var audio in nameStreams)
                {
                    var stereo = (audio.ChannelStreams?.Length == 2);
                    if ((audio.StreamBlocks != null) && (!stereo)) continue;//don't display multichannel source audios
                    var name = audio.Name + $" (0x{audio.Hash})";
                    if (stereo) continue; // name = "(Stereo Playback)";

                    filteredStreams.Add(audio);
                }

                if (Awc.MultiChannelFlag)
                {
                    while (filteredStreams.Count > 0)
                    {
                        List<AwcStream> objectsToRemove = filteredStreams
                        .GroupBy(obj => GetBaseName(obj.Name)) // Group by the base name
                        .Where(group => group.Count() > 1)     // Filter groups with more than one item
                        .SelectMany(group => group.Skip(0))    // Select all but the first item in each group
                        .ToList();

                        if (objectsToRemove.Count != 0)
                        {
                            StreamList.Items.Add(new StereoItemInfo(objectsToRemove[0], objectsToRemove[1]));
                            originalItems.Add(new StereoItemInfo(objectsToRemove[0], objectsToRemove[1]));

                            filteredStreams.Remove(objectsToRemove[0]);
                            filteredStreams.Remove(objectsToRemove[1]);
                        }
                        else if (filteredStreams.Count > 0)
                        {
                            foreach (var audio in filteredStreams)
                            {
                                StreamList.Items.Add(new ItemInfo(audio));
                                originalItems.Add(new ItemInfo(audio));
                            }

                            break;
                        }
                        
                    }
                }
                else
                {
                    foreach (var audio in filteredStreams)
                    {
                        StreamList.Items.Add(new ItemInfo(audio));
                        originalItems.Add(new ItemInfo(audio));
                    }
                }
         
                SaveButton.IsEnabled = true;
                RenameButton.IsEnabled = true;
                ReplaceButton.IsEnabled = true;
                AutoPlayBox.IsEnabled = true;
                slider.IsEnabled = true;
                PlayLastButton.IsEnabled = true;
                PlayButton.IsEnabled = true;
                PlayNextButton.IsEnabled = true;
                StreamList.IsEnabled = true;
                searchTextBox.IsEnabled = true;
                MoreOptionsButton.IsEnabled = true;
                StreamList.SelectedIndex = 0;
                currentPlayingIndex = 0;
            }
        }

        private void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (Awc == null) return;
            var window = new ReplaceAudioWindow(Awc.MultiChannelFlag);
            var result = window.ShowDialog();

            if (result == true)
            {
                if (Awc.MultiChannelFlag)
                {
                    for (int i = 0; i < StreamList.SelectedItems.Count; i++)
                    {
                        if (Awc?.Streams == null) continue;
                        if (StreamList.SelectedItems[i] is StereoItemInfo stereoInfo)
                        {
                            var pcmdata = window.PcmData;
                            if (!window.StereoInput)
                            {
                                pcmdata = MonoToStereo(pcmdata); //Convert mono input to stereo interleaved buffer
                            }
                            Awc?.ReplaceAudioStreamStereo(stereoInfo.StreamLeft.Hash, stereoInfo.StreamRight.Hash, (uint)window.SampleCount, (uint)window.SampleRate, pcmdata, window.CodecType);
                        }
                        else if (StreamList.SelectedItems[i] is ItemInfo monoInfo)
                        {
                            var pcmdata = window.PcmData;
                            if (window.StereoInput)
                            {
                                pcmdata = MixStereoToMono(pcmdata); //Convert stereo input to mono buffer
                            }
                            Awc?.ReplaceAudioStreamSingle(monoInfo.Stream.Hash, (uint)window.SampleCount, (uint)window.SampleRate, pcmdata, window.CodecType);
                        }
                    }

                    RefreshList();
                    return;
                }

                //Non-multichannel AWC
                for (int i = 0; i < StreamList.SelectedItems.Count; i++)
                {
                    if (Awc?.Streams == null) continue;
                    if (StreamList.SelectedItems[i] is ItemInfo item)
                    {
                        var pcmdata = window.PcmData;
                        if (window.StereoInput)
                        {
                            pcmdata = MixStereoToMono(pcmdata);
                        }
                        Awc?.ReplaceAudioStreamSingle(item.Stream.Hash, (uint)window.SampleCount, (uint)window.SampleRate, pcmdata, window.CodecType);
                    }
                }
            }
            RefreshList();
        }

        private void Play()
        {
            if (waveOut != null && waveOut.PlaybackState == PlaybackState.Playing) return;

            PlayButton.Content = "⏸";
            PlayButton.Click -= playHandler;
            PlayButton.Click += pauseHandler;

            if (isPaused && waveOut != null)
            {
                waveOut.Play();
                isPaused = false;
                return;
            }

            if (StreamList.SelectedItems.Count != 1) return;
            StopPlayback(); //Dispose old playback, but don’t nuke UI

            currentPlayingIndex = StreamList.SelectedIndex;
            double lengthSeconds;
            var item = StreamList.Items[currentPlayingIndex];

            waveOut = new WasapiOut(AudioClientShareMode.Exclusive, true, 50); //Fresh waveOut each time
            waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
            waveOut.Volume = (float)(VolumeSlider.Value / 100.0);

            if (item is StereoItemInfo stereo)
            {
                lengthSeconds = stereo.StreamLeft.Length;
                var leftPcm = stereo.StreamLeft.GetPcmData();
                var rightPcm = stereo.StreamRight.GetPcmData();
                var stereoPcm = CombineLeftAndRightChannel(leftPcm, rightPcm);
                sourceWaveStream = new RawSourceWaveStream(new MemoryStream(stereoPcm), new WaveFormat(stereo.StreamLeft.SamplesPerSecond, 16, 2));
            }
            else if (item is ItemInfo audio)
            {
                lengthSeconds = audio.Stream.Length;
                var pcmData = audio.Stream.GetPcmData();
                var channels = audio.Stream.StreamFormatChunk?.ChannelCount ?? 1;
                var waveFormat = new WaveFormat(audio.Stream.SamplesPerSecond, 16, (int)channels);
                sourceWaveStream = new RawSourceWaveStream(new MemoryStream(pcmData), waveFormat);
            }
            else return;

            waveOut.Init(sourceWaveStream);
            waveOut.Play();

            timer.Start();
            slider.Maximum = lengthSeconds;
            slider.Value = 0;

            PlayButton.Content = "⏸";
            PlayButton.Click -= playHandler;
            PlayButton.Click += pauseHandler;
        }

        private void Pause()
        {
            if (waveOut != null && waveOut.PlaybackState == PlaybackState.Playing)
            {
                waveOut.Pause();
                isPaused = true;
                PlayButton.Content = "▶";
                PlayButton.Click -= pauseHandler;
                PlayButton.Click += playHandler;
            }
        }

        private void ResetPlayerUI()
        {
            PlayButton.Content = "▶";
            PlayButton.Click -= pauseHandler;
            PlayButton.Click -= playHandler;
            PlayButton.Click += playHandler;

            slider.Value = 0;
            slider.Maximum = 1;
            DurationLabel.Content = "00:00 / 00:00";

            sourceWaveStream = null;
        }

        private void Stop()
        {
            isPaused = false;
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (sourceWaveStream != null)
            {
                sourceWaveStream.Dispose();
                sourceWaveStream = null;
            }

            timer.Stop();
            ResetPlayerUI();
        }

        private void StopPlayback() //Just stops playback, disposes waveOut and sourceWaveStream, but does not reset UI
        {
            isPaused = false;
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (sourceWaveStream != null)
            {
                sourceWaveStream.Dispose();
                sourceWaveStream = null;
            }
            timer.Stop();
        }

        private void ResetPlayer() //Full reset, UI, buttons, slider, etc
        {
            StopPlayback();
            ResetPlayerUI();
        }

        private void PlayNext()
        {
            if (currentPlayingIndex < StreamList.Items.Count - 1)
            {
                Stop();
                currentPlayingIndex++;
                StreamList.SelectedIndex = currentPlayingIndex;
                Play();
            }
            else
            {
                StreamList.SelectedIndex = 0;
                currentPlayingIndex = 0;
                Play();
            }
        }

        private void PlayLast()
        {
            if (currentPlayingIndex > 0)
            {
                Stop();
                currentPlayingIndex--;
                StreamList.SelectedIndex = currentPlayingIndex;
                Play();
            }
            else if (currentPlayingIndex == 0)
            {
                currentPlayingIndex = StreamList.Items.Count - 1;
                StreamList.SelectedIndex = StreamList.Items.Count - 1;
                Play();
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (waveOut != null && sourceWaveStream != null && waveOut.PlaybackState == PlaybackState.Playing)
            {
                var currentPosition = sourceWaveStream.Position / (double)sourceWaveStream.WaveFormat.AverageBytesPerSecond;
                slider.Value = currentPosition;

                var currentTime = TimeSpan.FromSeconds(currentPosition);
                DurationLabel.Content = currentTime.ToString(@"mm\:ss") + " / "+ TimeSpan.FromSeconds(sourceWaveStream.Length / sourceWaveStream.WaveFormat.AverageBytesPerSecond).ToString(@"mm\:ss");
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            Play();
        }

        private void PlayLastButton_Click(object sender, RoutedEventArgs e)
        {
            PlayLast();
        }

        private void PlayNextButton_Click(object sender, RoutedEventArgs e)
        {
            PlayNext();
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            Pause();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sourceWaveStream != null && (slider.IsFocused || slider.IsMouseDirectlyOver || slider.IsMouseOver || slider.IsKeyboardFocused || slider.IsKeyboardFocusWithin))
            {
                sourceWaveStream.SetPosition(slider.Value);
            }
        }

        private void TabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabControl)
            {
                //Check which tab is currently selected, more specifically if it's the player tab.  We do this to refresh the list when they switch back
                if (tabControl.SelectedItem == AwcPlayerTab)
                    RefreshList();
                else if (tabControl.SelectedItem == AwcXmlTab)
                    AwcXmlTextBox.Text = AwcXml.GetXml(Awc);
            }
        }

        private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (waveOut != null && waveOut.PlaybackState == PlaybackState.Playing) return;
            if (StreamList.SelectedItems.Count != 1) return;

            var item = StreamList.SelectedItem;
            double lengthSeconds = item switch
            {
                ItemInfo mono => mono.Stream.Length,
                StereoItemInfo stereo => stereo.StreamLeft.Length,
                _ => 0
            };

            if (lengthSeconds > 0)
            {
                var duration = TimeSpan.FromSeconds(lengthSeconds);
                DurationLabel.Content = $"00:00 / {duration:mm\\:ss}";
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (waveOut != null)
            {
                waveOut.Volume = (float)(VolumeSlider.Value / 100.0);
            }

            if (VolumeLabel != null)
            {
                VolumeLabel.Content = $"{VolumeSlider.Value:0}";
            }
        }

        private void VolumeResetButton_Click(object sender, RoutedEventArgs e)
        {
            VolumeSlider.Value = 15;
        }

        private void AutoPlay_Checked(object sender, RoutedEventArgs e)
        {
            autoPlayEnabled = true;
            LoopAutoPlay.IsEnabled = true;
        }

        private void AutoPlay_Unchecked(object sender, RoutedEventArgs e)
        {
            autoPlayEnabled = false;
            autoPlayLoopEnabled = false;
            LoopAutoPlay.IsEnabled = false;
        }

        private void LoopAutoPlay_Checked(object sender, RoutedEventArgs e)
        {
            autoPlayLoopEnabled = true;
        }

        private void LoopAutoPlay_Unchecked(object sender, RoutedEventArgs e)
        {
            autoPlayLoopEnabled = false;
        }

        private void MenuItemOption1_Click(object sender, RoutedEventArgs e)
        {
            using var folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();
            if (folderBrowserDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string exportPath = folderBrowserDialog.SelectedPath;
            var selectedItems = StreamList.SelectedItems.Cast<object>().ToList();

            foreach (var item in selectedItems)
            {
                string fileName;
                WaveFormat format;
                byte[] pcmData;

                if (item is ItemInfo monoItem)
                {
                    fileName = monoItem.Name;
                    pcmData = monoItem.Stream.GetPcmData();
                    format = new WaveFormat(monoItem.Stream.SamplesPerSecond, 16, 1);
                }
                else if (item is StereoItemInfo stereoItem)
                {
                    fileName = stereoItem.Name;
                    pcmData = CombineLeftAndRightChannel(stereoItem.StreamLeft.GetPcmData(), stereoItem.StreamRight.GetPcmData());
                    format = new WaveFormat(stereoItem.StreamLeft.SamplesPerSecond, 16, 2);
                }
                else
                {
                    continue;
                }

                string filePath = Path.Combine(exportPath, $"{fileName}.wav");
                using var writer = new WaveFileWriter(filePath, format);
                writer.Write(pcmData, 0, pcmData.Length);
            }

            string message = (selectedItems.Count == 1)
                ? $"Exported {((selectedItems[0] is ItemInfo mi) ? mi.Name : ((StereoItemInfo)selectedItems[0]).Name)} to {exportPath}"
                : $"Exported {selectedItems.Count} audio tracks to {exportPath}";

            MessageBox.Show(message, "Audio Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var keyword = searchTextBox.Text.ToLower();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                if (StreamList != null && originalItems != null)
                {
                    StreamList.Items.Clear();
                    foreach (var item in originalItems)
                    {
                        StreamList.Items.Add(item);
                    }
                }
            }
            else
            {
                if (StreamList != null && originalItems != null)
                {
                    var filteredItems = new List<object>();
                    foreach (var item in originalItems)
                    {
                        if (item is ItemInfo itemInfo && itemInfo.Name.ToLower().Contains(keyword))
                            filteredItems.Add(item);
                        else if (item is StereoItemInfo stereoItemInfo && stereoItemInfo.Name.ToLower().Contains(keyword))
                            filteredItems.Add(stereoItemInfo);
                    }

                    StreamList.Items.Clear();
                    foreach (var item in filteredItems)
                    {
                        StreamList.Items.Add(item);
                    }
                }
            }

            if (StreamList != null && StreamList.ItemsSource != null) //Don't want to refresh or access ItemsSource before it's set
            {
                CollectionViewSource.GetDefaultView(StreamList.ItemsSource).Refresh();
            }
        }

        private void MoreOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();

            if (folderBrowserDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            string folderPath = folderBrowserDialog.SelectedPath;

            string[] files = Directory.GetFiles(folderPath, "*.wav", SearchOption.TopDirectoryOnly);

            foreach (string file in files)
            {
                foreach (var stream in Awc.Streams)
                {
                    if (stream.Name.ToLower().Equals(Path.GetFileNameWithoutExtension(file).ToLower()))
                    {
                        WaveFileReader wavFile = new WaveFileReader(file);

                        WaveFormat format = new WaveFormat(wavFile.WaveFormat.SampleRate, 16, wavFile.WaveFormat.Channels);

                        MemoryStream outputStream = new MemoryStream();

                        MediaFoundationResampler resampler = new MediaFoundationResampler(wavFile, format);

                        byte[] array = new byte[resampler.WaveFormat.AverageBytesPerSecond * 4];
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

                        byte[] bytes = outputStream.ToArray();

                        outputStream.Dispose();

                        Awc?.ReplaceAudioStreamSingle(stream.Hash, (uint)(bytes.Length / 2), (uint)wavFile.WaveFormat.SampleRate, bytes, AwcCodecType.PCM);
                    }
                } 
            }

            RefreshList();
        }

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.Invoke(() => //This runs when NAudio is fully done
            {
                if (autoPlayEnabled)
                {
                    object? item = null;
                    if (currentPlayingIndex == (StreamList.Items.Count - 1))
                    {
                        if (autoPlayLoopEnabled)
                        {
                            currentPlayingIndex = 0;
                            item = StreamList.Items[currentPlayingIndex];
                            StreamList.SelectedItem = item;
                        }
                    }
                    else if (currentPlayingIndex < (StreamList.Items.Count - 1))
                    {
                        currentPlayingIndex++;
                        item = StreamList.Items[currentPlayingIndex];
                        StreamList.SelectedItem = item;
                    }

                    if (item != null)
                    {
                        StopPlayback(); //Safe dispose
                        Play();
                        return;
                    }
                }
                ResetPlayer(); //No autoplay so reset UI
            });
        }

        private static byte[] MonoToStereo(byte[] input)
        {
            var output = new byte[input.Length * 2];
            var outputIndex = 0;

            for (int n = 0; n < input.Length; n += 2)
            {
                output[outputIndex++] = input[n];
                output[outputIndex++] = input[n + 1];
                output[outputIndex++] = input[n];
                output[outputIndex++] = input[n + 1];
            }
            return output;
        }

        private static byte[] MixStereoToMono(byte[] input)
        {
            var output = new byte[input.Length / 2];
            var outputIndex = 0;

            for (int n = 0; n < input.Length; n += 4)
            {
                var leftChannel = BitConverter.ToInt16(input, n);
                var rightChannel = BitConverter.ToInt16(input, n + 2);
                var mixed = (leftChannel + rightChannel) / 2;
                var outSample = BitConverter.GetBytes((short)mixed);

                output[outputIndex++] = outSample[0];
                output[outputIndex++] = outSample[1];
            }
            return output;
        }

        private static byte[] CombineLeftAndRightChannel(byte[] left, byte[] right)
        {
            var minLength = Math.Min(left.Length, right.Length);
            var output = new byte[minLength * 2];
            var outputIndex = 0;

            for (int n = 0; n < minLength; n += 2)
            {
                output[outputIndex++] = left[n];
                output[outputIndex++] = left[n + 1];
                output[outputIndex++] = right[n];
                output[outputIndex++] = right[n + 1];
            }
            return output;
        }

        private static string GetBaseName(string fullName)
        {
            var lastUnderscoreIndex = fullName.LastIndexOf('_');
            if (lastUnderscoreIndex >= 0)
            {
                return fullName.Substring(0, lastUnderscoreIndex);
            }
            return fullName;
        }
    }
}