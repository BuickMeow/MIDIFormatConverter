using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Tools;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.IO.MemoryMappedFiles;

namespace MIDIFormatConverter
{
    public partial class MainWindow : Window
    {
        private string _inputPath = string.Empty;
        private string _outputPath = string.Empty;
        private readonly Stopwatch _stopwatch = new();
        private DispatcherTimer? _timer;

        public MainWindow()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            InitializeComponent();
        }

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "MIDI文件 (*.mid)|*.mid|所有文件 (*.*)|*.*",
                Title = "选择要转换的MIDI文件"
            };

            if (dialog.ShowDialog() == true)
            {
                _inputPath = dialog.FileName;
                txtFilePath.Text = _inputPath;
                statusText.Text = "文件已就绪";
            }
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            // 如果未选择 MIDI 文件，则自动弹出选择对话框
            if (string.IsNullOrEmpty(_inputPath))
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "MIDI文件 (*.mid)|*.mid|所有文件 (*.*)|*.*",
                    Title = "选择要转换的MIDI文件"
                };

                if (dialog.ShowDialog() == true)
                {
                    _inputPath = dialog.FileName;
                    txtFilePath.Text = _inputPath;
                    statusText.Text = "文件已就绪";
                }
                else
                {
                    // 如果用户取消选择，则直接返回
                    return;
                }
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "MIDI格式0文件 (*.mid)|*.mid",
                FileName = $"{Path.GetFileNameWithoutExtension(_inputPath)}_converted.mid"
            };

            if (saveDialog.ShowDialog() == true)
            {
                _outputPath = saveDialog.FileName;

                // 启动计时
                _stopwatch.Reset();
                _stopwatch.Start();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (s, ev) =>
                {
                    btnConvert.Content = $"转换中({_stopwatch.Elapsed:mm\\:ss})";
                };
                _timer.Start();
                await ConvertMidiAsync(_inputPath, _outputPath, 1024);

                // 转换完成后停止计时，并恢复按钮文本
                _timer?.Stop();
                _stopwatch.Stop();
                btnConvert.Content = "开始转换";
                MessageBox.Show($"文件已保存至：{_outputPath}\n总用时: {_stopwatch.Elapsed:mm\\:ss}",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task ConvertMidiAsync(string inputPath, string outputPath, decimal maxMemory)
        {
            try
            {
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 0;

                // 创建一个计时器实时更新“正在读取MIDI文件...”状态和内存使用
                var readingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                readingTimer.Tick += (s, ev) =>
                {
                    statusText.Text = $"正在读取MIDI文件... 内存使用: {(GC.GetTotalMemory(false) / (1024.0 * 1024.0)):F2} MB";
                };
                readingTimer.Start();

                // 在后台线程读取 MIDI 文件
                MidiFile? midiFile = null;
                await Task.Run(() =>
                {
                    midiFile = MidiFile.Read(inputPath);
                });
                readingTimer.Stop();

                // 开始转换处理
                await Task.Run(() =>
                {
                    var newTracks = new List<TrackChunk>();
                    var trackChunks = midiFile.GetTrackChunks().ToList();
                    int totalTracks = trackChunks.Count;
                    int currentTrack = 0;

                    foreach (var track in trackChunks)
                    {
                        // 更新当前音轨读取状态
                        Dispatcher.Invoke(() =>
                        {
                            statusText.Text = $"正在读取 {currentTrack + 1}/{totalTracks} 条音轨, 内存使用: {(GC.GetTotalMemory(false) / (1024.0 * 1024.0)):F2} MB";
                        });

                        var timedEvents = GetTimedEvents(track).ToList();
                        var channelGroups = timedEvents
                            .Where(te => te.Event is ChannelEvent)
                            .Select(te => ((ChannelEvent)te.Event).Channel)
                            .Distinct()
                            .ToList();

                        if (channelGroups.Count <= 1)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                statusText.Text = $"正在转换 {currentTrack + 1}/{totalTracks} 条音轨, 内存使用: {(GC.GetTotalMemory(false) / (1024.0 * 1024.0)):F2} MB";
                            });
                            newTracks.Add(RecalculateDeltaTimes(timedEvents));
                        }
                        else
                        {
                            foreach (var channel in channelGroups)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    statusText.Text = $"正在转换 {currentTrack + 1}/{totalTracks} 条音轨 (Channel {channel}), 内存使用: {(GC.GetTotalMemory(false) / (1024.0 * 1024.0)):F2} MB";
                                });
                                var filtered = timedEvents
                                    .Where(te => te.Event is not ChannelEvent || (te.Event is ChannelEvent ce && ce.Channel == channel))
                                    .OrderBy(te => te.Time)
                                    .ToList();
                                newTracks.Add(RecalculateDeltaTimes(filtered));
                                filtered.Clear(); // 尽快释放内存
                            }
                        }
                        timedEvents.Clear(); // 释放当前音轨临时集合

                        currentTrack++;
                        Dispatcher.Invoke(() =>
                        {
                            progressBar.Value = (currentTrack * 100) / totalTracks;
                            statusText.Text += $"  ({currentTrack}/{totalTracks})";
                        });

                        // 如果当前内存占用超过阈值，则触发垃圾回收，并等待完成
                        long currentMemory = GC.GetTotalMemory(false);
                        if (currentMemory > maxMemory * 1024 * 1024)
                        {
                            System.GC.Collect();
                            System.GC.WaitForPendingFinalizers();
                            Task.Delay(50).Wait();
                        }
                    }

                    Dispatcher.Invoke(() => statusText.Text = "正在生成文件...");

                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }

                    // 指定 UTF8 编码写入
                    var writingSettings = new WritingSettings { TextEncoding = System.Text.Encoding.UTF8 };
                    using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                    new MidiFile(newTracks) { TimeDivision = midiFile.TimeDivision }
                        .Write(stream: fs, settings: writingSettings);
                });

                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = 100;
                    statusText.Text = "转换完成！";
                });
            }
            catch (System.Exception ex)
            {
                Dispatcher.Invoke(() => statusText.Text = "转换失败");
                MessageBox.Show($"转换出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Dispatcher.Invoke(() => progressBar.Visibility = Visibility.Collapsed);
            }
        }

        /// <summary>
        /// 计算音轨中各事件的绝对时间
        /// </summary>
        private static IEnumerable<TimedEvent> GetTimedEvents(TrackChunk trackChunk)
        {
            long cumulativeTime = 0;
            foreach (var midiEvent in trackChunk.Events)
            {
                cumulativeTime += midiEvent.DeltaTime;
                yield return new TimedEvent(midiEvent, cumulativeTime);
            }
        }

        /// <summary>
        /// 根据绝对事件列表重新计算 DeltaTime，并生成新的音轨
        /// </summary>
        private static TrackChunk RecalculateDeltaTimes(List<TimedEvent> timedEvents)
        {
            var newEvents = new List<MidiEvent>();
            long prevTime = 0;
            foreach (var te in timedEvents.OrderBy(te => te.Time))
            {
                int delta = (int)(te.Time - prevTime);
                var midiEvent = te.Event.Clone();
                midiEvent.DeltaTime = delta;
                newEvents.Add(midiEvent);
                prevTime = te.Time;
            }
            return new TrackChunk(newEvents);
        }

        // 使用 record 的主构造函数简化 TimedEvent 定义
        private record TimedEvent(MidiEvent Event, long Time);
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string info = "MIDI转换器 v0.3\n" +
                          "本软件用于将MIDI文件转换为格式0的MIDI文件。\n" +
                          "可以通过这个软件来将Domino不能打开的\n" +
                          "“一轨多通道”MIDI转换为Domino可以打开的格式。\n" +
                          "支持多种音轨的处理，并实时显示转换状态。\n" +
                          "对于低内存用户友好，500MB的MIDI会占用大约7G内存。\n\n" +
                          "作者：节能降耗";
            MessageBox.Show(info, "关于本软件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}