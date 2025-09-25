using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChatClientWpf
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;

    public partial class MainWindow : Window
    {
        private TcpClient? _tcp;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;

        // Typing indicator state
        private bool _isTypingLocal = false;
        private readonly HashSet<string> _typingUsers = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _typingIdleTimer; // stop typing jika idle

        public MainWindow()
        {
            InitializeComponent();

            TbName.Text = $"User{Random.Shared.Next(100, 999)}";

            // Default tema Light
            ApplyTheme(isDark: false);

            // Init timer typing
            InitTypingTimer();
        }

        #region Theme
        private void ApplyTheme(bool isDark)
        {
            // Ambil ResourceDictionary bertanda LightTheme/DarkTheme dari Window.Resources
            Resources.MergedDictionaries.Clear();
            var dict = (ResourceDictionary)Resources[isDark ? "DarkTheme" : "LightTheme"];
            Resources.MergedDictionaries.Add(dict);
        }

        private void BtnTheme_Checked(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isDark: true);
            BtnTheme.Content = "Light";
        }

        private void BtnTheme_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyTheme(isDark: false);
            BtnTheme.Content = "Dark";
        }
        #endregion

        #region Connect / Disconnect
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_tcp != null) return;

            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(TbIp.Text.Trim(), int.Parse(TbPort.Text.Trim()));
                _tcp.NoDelay = true;

                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
                _cts = new CancellationTokenSource();

                // handshake nama
                await _writer.WriteLineAsync($"SETNAME|{TbName.Text.Trim()}");

                ToggleUi(true);
                AppendChat($"[you] connected to {TbIp.Text}:{TbPort.Text}");

                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                AppendChat($"[error] connect failed: {ex.Message}");
                Disconnect();
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

        private void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _reader = null;
            _writer = null;
            _tcp = null;
            _cts = null;

            _typingUsers.Clear();
            _isTypingLocal = false;

            ToggleUi(false);
            AppendChat("[you] disconnected");
            LvUsers.Items.Clear();
        }

        private void ToggleUi(bool connected)
        {
            BtnConnect.IsEnabled = !connected;
            BtnDisconnect.IsEnabled = connected;
            TbIp.IsEnabled = TbPort.IsEnabled = TbName.IsEnabled = !connected;
            TbInput.IsEnabled = BtnSend.IsEnabled = connected;
        }
        #endregion

        #region Receive / Parse
        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line is null) break;

                    if (line.StartsWith("SYS|"))
                    {
                        AppendChat("[sys] " + line[4..]);
                    }
                    else if (line.StartsWith("USERLIST|"))
                    {
                        var s = line[9..];
                        var arr = s.Length == 0 ? Array.Empty<string>() : s.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        Dispatcher.Invoke(() =>
                        {
                            LvUsers.Items.Clear();
                            foreach (var u in arr)
                                LvUsers.Items.Add(RenderUser(u));
                        });
                    }
                    else if (line.StartsWith("MSG|"))
                    {
                        var rest = line[4..];
                        var sep = rest.IndexOf('|');
                        if (sep > 0)
                        {
                            var from = rest[..sep];
                            var text = rest[(sep + 1)..];
                            AppendChat($"[{from}] {text}");
                        }
                    }
                    else if (line.StartsWith("WHISPER|"))
                    {
                        var parts = line.Split('|', 4);
                        if (parts.Length == 4)
                        {
                            var from = parts[1];
                            var to = parts[2];
                            var text = parts[3];
                            AppendChat($"(PM {from}→{to}) {text}");
                        }
                    }
                    else if (line.StartsWith("TYPING|"))
                    {
                        // format: TYPING|<user>|1/0
                        var parts = line.Split('|');
                        if (parts.Length == 3)
                        {
                            var user = parts[1];
                            var on = parts[2] == "1";
                            Dispatcher.Invoke(() =>
                            {
                                if (on) _typingUsers.Add(user);
                                else _typingUsers.Remove(user);
                                RefreshUserListVisual();
                            });
                        }
                    }
                    else
                    {
                        AppendChat("[?] " + line);
                    }
                }
            }
            catch (IOException)
            {
                AppendChat("[error] connection closed.");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                AppendChat("[recv error] " + ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(Disconnect);
            }
        }
        #endregion

        #region Send / Input
        private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendFromInputAsync();

        private async void TbInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                await SendFromInputAsync();
            }
        }

        private async Task SendFromInputAsync()
        {
            var text = TbInput.Text;
            if (string.IsNullOrWhiteSpace(text) || _writer is null) return;

            TbInput.Clear();

            // format PM: /w <user> <text>
            if (text.StartsWith("/w "))
            {
                var rest = text[3..].Trim();
                var sp = rest.IndexOf(' ');
                if (sp > 0)
                {
                    var to = rest[..sp];
                    var msg = rest[(sp + 1)..];
                    await SafeWriteAsync($"WHISPER|{to}|{msg}");
                    StopTyping();
                    return;
                }
            }

            await SafeWriteAsync($"MSG|{text}");
            StopTyping();
        }

        private async Task SafeWriteAsync(string line)
        {
            try
            {
                if (_writer != null)
                    await _writer.WriteLineAsync(line);
            }
            catch (Exception ex)
            {
                AppendChat("[send error] " + ex.Message);
            }
        }
        #endregion

        #region Typing indicator
        private void InitTypingTimer()
        {
            _typingIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _typingIdleTimer.Tick += (_, __) => StopTyping();
        }

        private async void TbInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_writer == null) return;

            if (string.IsNullOrEmpty(TbInput.Text))
            {
                StopTyping();
                return;
            }

            if (!_isTypingLocal)
            {
                _isTypingLocal = true;
                await SafeWriteAsync("TYPING|1");
            }

            _typingIdleTimer!.Stop();
            _typingIdleTimer.Start();
        }

        private async void StopTyping()
        {
            if (!_isTypingLocal) return;
            _isTypingLocal = false;
            _typingIdleTimer?.Stop();
            await SafeWriteAsync("TYPING|0");
        }
        #endregion

        #region UI helpers (user list & chat)
        private void AppendChat(string line)
        {
            Dispatcher.Invoke(() =>
            {
                TbChat.AppendText(line + Environment.NewLine);
                TbChat.ScrollToEnd();
            });
        }

        private string RenderUser(string username)
            => _typingUsers.Contains(username) ? $"{username} (typing…)" : username;

        private void RefreshUserListVisual()
        {
            var raw = new List<string>();
            foreach (var item in LvUsers.Items)
            {
                var label = item?.ToString() ?? "";
                raw.Add(label.Split(" (")[0]); // ambil nama asli
            }
            LvUsers.Items.Clear();
            foreach (var u in raw)
                LvUsers.Items.Add(RenderUser(u));
        }

        private void LvUsers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LvUsers.SelectedItem is string label)
            {
                var name = label.Split(" (")[0];
                if (!name.Equals(TbName.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TbInput.Text = $"/w {name} ";
                    TbInput.CaretIndex = TbInput.Text.Length;
                    TbInput.Focus();
                }
            }
        }
        #endregion
    }
}
