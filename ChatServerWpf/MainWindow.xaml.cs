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

        public MainWindow()
        {
            InitializeComponent();
        }
// Tambah di MainWindow.xaml.cs (Server WPF)
        private void BroadcastUserList()
        {
            var users = string.Join(',', _clients.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Broadcast($"USERLIST|{users}");
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TbPort.Text, out int port)) return;

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Log($"[Server] Listening on port {port}");
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;

            try {
                while (!_cts.Token.IsCancellationRequested) {
                    var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(tcp);
                }
            }
            catch (OperationCanceledException) { }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _listener?.Stop();
            Log("[Server] Stopped.");
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            LbUsers.Items.Clear();
            _clients.Clear();
        }

        private async Task HandleClientAsync(TcpClient tcp)
        {
            var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
            var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            // handshake
            var first = await reader.ReadLineAsync();
            if (first is null || !first.StartsWith("SETNAME|")) {
                await writer.WriteLineAsync("SYS|Invalid handshake.");
                tcp.Close();
                return;
            }

            var username = first[8..].Trim();
            if (!_clients.TryAdd(username, new ClientConn(tcp, reader, writer))) {
                await writer.WriteLineAsync("SYS|Username already exists.");
                tcp.Close();
                return;
            }

            await writer.WriteLineAsync($"USERLIST|{string.Join(',', _clients.Keys)}");
            
            Dispatcher.Invoke(() => LbUsers.Items.Add(username));
            Broadcast($"SYS|{username} joined.");
            BroadcastUserList();                
            Log($"{username} connected.");

            try {
                while (true) {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("MSG|")) {
                        var text = line[4..];
                        Broadcast($"MSG|{username}|{text}");
                    } else if (line.StartsWith("WHISPER|")) {
                        var rest = line[8..];
                        var sep = rest.IndexOf('|');
                        if (sep > 0) {
                            var to = rest[..sep];
                            var text = rest[(sep + 1)..];
                            if (_clients.TryGetValue(to, out var target)) {
                                await target.Writer.WriteLineAsync($"WHISPER|{username}|{to}|{text}");
                                await writer.WriteLineAsync($"WHISPER|{username}|{to}|{text}");
                            }
                        }
                    }
                }
            }
            finally {
                _clients.TryRemove(username, out _);
                Dispatcher.Invoke(() => LbUsers.Items.Remove(username));
                Broadcast($"SYS|{username} left.");
                BroadcastUserList();           
                Log($"{username} disconnected.");
                tcp.Close();
            }
        }

        private void Broadcast(string line)
        {
            foreach (var c in _clients.Values) {
                try { c.Writer.WriteLine(line); }
                catch { }
            }
        }

        private void Log(string line)
        {
            Dispatcher.Invoke(() =>
            {
                TbLog.AppendText(line + Environment.NewLine);
                TbLog.ScrollToEnd();
            });
        }

        private sealed class ClientConn
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
        }
    }
}