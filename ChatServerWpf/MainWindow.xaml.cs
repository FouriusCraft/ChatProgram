using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace ChatServerWpf
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public partial class MainWindow : Window
    {
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<string, ClientConn> _clients = new();
        private CancellationTokenSource? _cts;

        // logging to file
        private StreamWriter? _logFile;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BroadcastUserList()
        {
            var users = string.Join(',', _clients.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Broadcast($"USERLIST|{users}");
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TbPort.Text, out int port)) return;

            try
            {
                Directory.CreateDirectory("logs");
                var logPath = Path.Combine("logs", $"server-{DateTime.Now:yyyyMMdd}.log");
                _logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Log($"[Server] Listening on port {port}");

                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled = true;

                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _ = HandleClientAsync(tcp);
                    }
                }
                catch (OperationCanceledException) { }
            }
            catch (Exception ex)
            {
                Log($"[Server] Start failed: {ex.Message}");
                _logFile?.Dispose();
                _logFile = null;
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled = false;
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            Log("[Server] Stopped.");

            // kick all clients gracefully
            foreach (var kv in _clients.ToArray())
            {
                try { kv.Value.Writer.WriteLine("SYS|Server stopping."); } catch { }
                try { kv.Value.Dispose(); } catch { }
            }
            _clients.Clear();

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            LbUsers.Items.Clear();

            _logFile?.Dispose();
            _logFile = null;
        }

        private async Task HandleClientAsync(TcpClient tcp)
        {
            using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
            using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // handshake
            string? first = null;
            try { first = await reader.ReadLineAsync(); }
            catch (Exception ex)
            {
                Log($"[Handshake error] {ex.Message}");
                tcp.Close();
                return;
            }

            if (first is null || !first.StartsWith("SETNAME|"))
            {
                await SafeWriteAsync(writer, "SYS|Invalid handshake.");
                tcp.Close();
                return;
            }

            var username = first[8..].Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                await SafeWriteAsync(writer, "SYS|Empty username.");
                tcp.Close();
                return;
            }

            if (!_clients.TryAdd(username, new ClientConn(tcp, reader, writer)))
            {
                await SafeWriteAsync(writer, "SYS|Username already exists.");
                tcp.Close();
                return;
            }

            await SafeWriteAsync(writer, $"USERLIST|{string.Join(',', _clients.Keys)}");
            Dispatcher.Invoke(() => LbUsers.Items.Add(username));
            Broadcast($"SYS|{username} joined.");
            BroadcastUserList();
            Log($"{username} connected.");

            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("MSG|"))
                    {
                        var text = line[4..];
                        Broadcast($"MSG|{username}|{text}");
                    }
                    else if (line.StartsWith("WHISPER|"))
                    {
                        var rest = line[8..];
                        var sep = rest.IndexOf('|');
                        if (sep > 0)
                        {
                            var to = rest[..sep];
                            var text = rest[(sep + 1)..];
                            if (_clients.TryGetValue(to, out var target))
                            {
                                await SafeWriteAsync(target.Writer, $"WHISPER|{username}|{to}|{text}");
                                await SafeWriteAsync(writer, $"WHISPER|{username}|{to}|{text}");
                            }
                        }
                    }
                    else if (line.StartsWith("TYPING|"))
                    {
                        // client sends TYPING|1 or TYPING|0
                        var on = line.EndsWith("|1") || line.EndsWith("1");
                        Broadcast($"TYPING|{username}|{(on ? "1" : "0")}");
                    }
                }
            }
            catch (IOException) { /* client dropped */ }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log($"[client error] {username}: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(username, out _);
                Dispatcher.Invoke(() => LbUsers.Items.Remove(username));
                Broadcast($"SYS|{username} left.");
                BroadcastUserList();
                Log($"{username} disconnected.");
                try { tcp.Close(); } catch { }
            }
        }

        private void Broadcast(string line)
        {
            foreach (var c in _clients.Values.ToArray())
            {
                try { c.Writer.WriteLine(line); }
                catch (Exception ex) { Log($"[broadcast error] {ex.Message}"); }
            }
        }

        private async Task SafeWriteAsync(StreamWriter w, string line)
        {
            try { await w.WriteLineAsync(line); }
            catch (Exception ex) { Log($"[send error] {ex.Message}"); }
        }

        private void Log(string line)
        {
            var msg = $"{DateTime.Now:HH:mm:ss} {line}";
            Dispatcher.Invoke(() =>
            {
                TbLog.AppendText(msg + Environment.NewLine);
                TbLog.ScrollToEnd();
            });
            try { _logFile?.WriteLine(msg); } catch { }
            Console.WriteLine(msg);
        }

        private sealed class ClientConn : IDisposable
        {
            public TcpClient Tcp { get; }
            public StreamReader Reader { get; }
            public StreamWriter Writer { get; }

            public ClientConn(TcpClient tcp, StreamReader r, StreamWriter w)
            {
                Tcp = tcp;
                Reader = r;
                Writer = w;
            }

            public void Dispose()
            {
                try { Reader.Dispose(); } catch { }
                try { Writer.Dispose(); } catch { }
                try { Tcp.Close(); } catch { }
            }
        }
    }
}
