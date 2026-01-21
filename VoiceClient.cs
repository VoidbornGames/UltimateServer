using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class VoiceClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _serverEndPoint;
    private readonly WaveInEvent _waveIn;
    private readonly WaveOutEvent _waveOut;
    private readonly BufferedWaveProvider _waveProvider;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private bool _disposed = false;
    private Timer _heartbeatTimer;
    private string _currentRoom = null;
    private string _currentChannel = null;

    // Message types to match server
    private enum MessageType : byte
    {
        VoiceData = 0,
        JoinRoom = 1,
        LeaveRoom = 2,
        JoinChannel = 3,
        LeaveChannel = 4,
        RoomList = 5,
        ChannelList = 6,
        ServerMessage = 7,
        PacketData = 8
    }

    public VoiceClient(string serverIp, int port)
    {
        Console.WriteLine($"[CLIENT] Initializing... Server: {serverIp}:{port}");

        _udp = new UdpClient(0);
        _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), port);

        // Audio configuration
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16 kHz, 16-bit, mono
            BufferMilliseconds = 20                   // small latency, safe UDP size
        };
        _waveIn.DataAvailable += WaveIn_DataAvailable;

        _waveProvider = new BufferedWaveProvider(_waveIn.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(100),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_waveProvider);

        Console.WriteLine("[CLIENT] Initialized successfully.");
    }

    private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded == 0 || _currentRoom == null || _currentChannel == null) return;

        try
        {
            // Split into smaller chunks to avoid UDP packet drop
            int chunkSize = 1024; // 1 KB per packet
            for (int offset = 0; offset < e.BytesRecorded; offset += chunkSize)
            {
                int size = Math.Min(chunkSize, e.BytesRecorded - offset);

                // Create a new buffer with the message type byte
                byte[] packet = new byte[size + 1];
                packet[0] = (byte)MessageType.VoiceData;
                Array.Copy(e.Buffer, offset, packet, 1, size);

                _udp.Send(packet, packet.Length, _serverEndPoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR sending audio: {ex.Message}");
        }
    }

    private void SendHeartbeat(object state)
    {
        if (_disposed) return;

        try
        {
            string message = "heartbeat";
            byte[] data = Encoding.UTF8.GetBytes(message);
            _udp.Send(data, data.Length, _serverEndPoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR sending heartbeat: {ex.Message}");
        }
    }

    public async Task StartReceivingAsync()
    {
        Console.WriteLine("[CLIENT] Starting receive loop...");
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                if (!_disposed && result.Buffer.Length > 0)
                {
                    var messageType = (MessageType)result.Buffer[0];

                    switch (messageType)
                    {
                        case MessageType.VoiceData:
                            // Extract voice data (skip the message type byte)
                            byte[] voiceData = new byte[result.Buffer.Length - 1];
                            Array.Copy(result.Buffer, 1, voiceData, 0, voiceData.Length);
                            _waveProvider.AddSamples(voiceData, 0, voiceData.Length);
                            break;

                        case MessageType.ServerMessage:
                            string serverMessage = Encoding.UTF8.GetString(result.Buffer.Skip(1).ToArray());
                            Console.WriteLine($"[SERVER] {serverMessage}");
                            break;

                        case MessageType.RoomList:
                            string roomList = Encoding.UTF8.GetString(result.Buffer.Skip(1).ToArray());
                            Console.WriteLine($"[SERVER] Available rooms: {roomList}");
                            break;

                        case MessageType.ChannelList:
                            string channelList = Encoding.UTF8.GetString(result.Buffer.Skip(1).ToArray());
                            Console.WriteLine($"[SERVER] Available channels: {channelList}");
                            break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("[CLIENT] Receive loop stopped (UdpClient disposed).");
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[CLIENT] ERROR receiving data: {ex.Message}");
                }
            }
        }
    }

    public void Start()
    {
        Console.WriteLine("[CLIENT] Starting recording and playback...");
        try
        {
            _waveIn.StartRecording();
            _waveOut.Play();
            _ = StartReceivingAsync();

            // Start heartbeat timer
            _heartbeatTimer = new Timer(SendHeartbeat, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            Console.WriteLine("[CLIENT] Started successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] FATAL ERROR on Start(): {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _waveIn.StopRecording();
        _waveOut.Stop();
        _heartbeatTimer?.Dispose();
    }

    public async Task JoinRoomAsync(string roomId)
    {
        try
        {
            byte[] message = Encoding.UTF8.GetBytes(roomId);
            byte[] packet = new byte[message.Length + 1];
            packet[0] = (byte)MessageType.JoinRoom;
            Array.Copy(message, 0, packet, 1, message.Length);

            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            _currentRoom = roomId;
            Console.WriteLine($"[CLIENT] Sent join room request for: {roomId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR joining room: {ex.Message}");
        }
    }

    public async Task LeaveRoomAsync()
    {
        if (_currentRoom == null) return;

        try
        {
            byte[] packet = new byte[1] { (byte)MessageType.LeaveRoom };
            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            Console.WriteLine($"[CLIENT] Sent leave room request for: {_currentRoom}");
            _currentRoom = null;
            _currentChannel = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR leaving room: {ex.Message}");
        }
    }

    public async Task JoinChannelAsync(string channelId)
    {
        if (_currentRoom == null)
        {
            Console.WriteLine("[CLIENT] ERROR: You must join a room first");
            return;
        }

        try
        {
            byte[] message = Encoding.UTF8.GetBytes(channelId);
            byte[] packet = new byte[message.Length + 1];
            packet[0] = (byte)MessageType.JoinChannel;
            Array.Copy(message, 0, packet, 1, message.Length);

            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            _currentChannel = channelId;
            Console.WriteLine($"[CLIENT] Sent join channel request for: {channelId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR joining channel: {ex.Message}");
        }
    }

    public async Task LeaveChannelAsync()
    {
        if (_currentChannel == null) return;

        try
        {
            byte[] packet = new byte[1] { (byte)MessageType.LeaveChannel };
            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            Console.WriteLine($"[CLIENT] Sent leave channel request for: {_currentChannel}");
            _currentChannel = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR leaving channel: {ex.Message}");
        }
    }

    public async Task RequestRoomListAsync()
    {
        try
        {
            byte[] packet = new byte[1] { (byte)MessageType.RoomList };
            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            Console.WriteLine("[CLIENT] Sent room list request");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR requesting room list: {ex.Message}");
        }
    }

    public async Task RequestChannelListAsync(string roomId)
    {
        try
        {
            byte[] message = Encoding.UTF8.GetBytes(roomId);
            byte[] packet = new byte[message.Length + 1];
            packet[0] = (byte)MessageType.ChannelList;
            Array.Copy(message, 0, packet, 1, message.Length);

            await _udp.SendAsync(packet, packet.Length, _serverEndPoint);
            Console.WriteLine($"[CLIENT] Sent channel list request for room: {roomId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] ERROR requesting channel list: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Stop();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _udp?.Close();
        _udp?.Dispose();
        _cts?.Dispose();
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Voice Chat Client");
        Console.WriteLine("=================");

        // Get server IP and port
        Console.Write("Enter server IP: ");
        string serverIp = Console.ReadLine();

        Console.Write("Enter server port: ");
        int port = int.Parse(Console.ReadLine());

        // Create and start the client
        using var client = new VoiceClient(serverIp, port);
        client.Start();

        // Simple command interface
        Console.WriteLine("\nCommands:");
        Console.WriteLine("  joinroom <roomid>    - Join a room");
        Console.WriteLine("  leaveroom            - Leave current room");
        Console.WriteLine("  joinchannel <id>     - Join a channel");
        Console.WriteLine("  leavechannel         - Leave current channel");
        Console.WriteLine("  rooms                - List available rooms");
        Console.WriteLine("  channels <roomid>    - List channels in a room");
        Console.WriteLine("  quit                 - Exit the application");
        Console.WriteLine("");

        // Command loop
        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine().Trim().ToLower();

            if (string.IsNullOrEmpty(input)) continue;

            string[] parts = input.Split(' ', 2);
            string command = parts[0];

            switch (command)
            {
                case "joinroom":
                    if (parts.Length > 1)
                        await client.JoinRoomAsync(parts[1]);
                    else
                        Console.WriteLine("Usage: joinroom <roomid>");
                    break;

                case "leaveroom":
                    await client.LeaveRoomAsync();
                    break;

                case "joinchannel":
                    if (parts.Length > 1)
                        await client.JoinChannelAsync(parts[1]);
                    else
                        Console.WriteLine("Usage: joinchannel <channelid>");
                    break;

                case "leavechannel":
                    await client.LeaveChannelAsync();
                    break;

                case "rooms":
                    await client.RequestRoomListAsync();
                    break;

                case "channels":
                    if (parts.Length > 1)
                        await client.RequestChannelListAsync(parts[1]);
                    else
                        Console.WriteLine("Usage: channels <roomid>");
                    break;

                case "quit":
                    return;

                default:
                    Console.WriteLine($"Unknown command: {command}");
                    break;
            }
        }
    }
}