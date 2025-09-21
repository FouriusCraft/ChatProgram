using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ChatClientWpf
{
    using System;
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

        public MainWindow()
        {
            InitializeComponent();
            TbName.Text = $"User{Random.Shared.Next(100, 999)}";
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_tcp != null) return;
            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(TbIp.Text.Trim(), int.Parse(TbPort.Text.Trim()));
                _tcp.NoDelay = true;

                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _cts = new CancellationTokenSource();

                await _writer.WriteLineAsync($"SETNAME|{TbName.Text.Trim()}");

                ToggleUi(true);
                AppendChat($"[you] connected to {TbIp.Text}:{TbPort.Text}");

                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                AppendChat($"[error] {ex.Message}");
                Disconnect();
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

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
                            LbUsers.Items.Clear();
                            foreach (var u in arr) LbUsers.Items.Add(u);
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
                    else
                    {
                        AppendChat("[?] " + line);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AppendChat("[recv error] " + ex.Message); }
            finally { Dispatcher.Invoke(Disconnect); }
        }

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

            // "/w <user> <text>"
            if (text.StartsWith("/w "))
            {
                var rest = text[3..].Trim();
                var sp = rest.IndexOf(' ');
                if (sp > 0)
                {
                    var to = rest[..sp];
                    var msg = rest[(sp + 1)..];
                    await _writer.WriteLineAsync($"WHISPER|{to}|{msg}");
                    return;
                }
            }

            await _writer.WriteLineAsync($"MSG|{text}");
        }

        private void AppendChat(string line)
        {
            Dispatcher.Invoke(() =>
            {
                TbChat.AppendText(line + Environment.NewLine);
                TbChat.ScrollToEnd();
            });
        }

        private void ToggleUi(bool connected)
        {
            BtnConnect.IsEnabled = !connected;
            BtnDisconnect.IsEnabled = connected;
            TbIp.IsEnabled = TbPort.IsEnabled = TbName.IsEnabled = !connected;
            TbInput.IsEnabled = BtnSend.IsEnabled = connected;
        }

        private void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _reader = null; _writer = null; _tcp = null; _cts = null;

            ToggleUi(false);
            AppendChat("[you] disconnected");
            LbUsers.Items.Clear();
        }
    }
}
