using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Servers
{
    internal class Channel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ConcurrentDictionary<IPEndPoint, bool> Clients { get; set; } = new();
    }

    internal class Room
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ConcurrentDictionary<string, Channel> Channels { get; set; } = new();
        public ConcurrentDictionary<IPEndPoint, string> ClientChannels { get; set; } = new();

        public Room(string id, string name)
        {
            Id = id;
            Name = name;
            var defaultChannel = new Channel { Id = "general", Name = "General" };
            Channels.TryAdd("general", defaultChannel);
        }
    }

    internal enum MessageType : byte
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

    internal class UdpServer
    {
        private readonly int _port;
        private readonly int _maxPacketSize;
        private readonly Logger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private UdpClient _udpClient;

        private readonly ConcurrentDictionary<IPEndPoint, string> _clientRooms = new();

        private readonly ConcurrentDictionary<string, Room> _rooms = new();

        public UdpServer(ServerSettings settings, ConfigManager configManager, Logger logger)
        {
            _maxPacketSize = configManager.Config.MaxRequestSizeMB * 1024 * 3;
            _port = settings.udpPort;
            _logger = logger;

            var defaultRoom = new Room("lobby", "Lobby");
            _rooms.TryAdd("lobby", defaultRoom);
        }

        /// <summary>
        /// Starts the UDP server and begins listening for voice packets and control messages.
        /// </summary>
        public async Task Start()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
                    _logger.Log($"🎧 Voice UDP server started on port {_port}");
                }
                catch (SocketException ex)
                {
                    _logger.Log($"FATAL ERROR: Could not start server on port {_port}. Is it already in use? Error: {ex.Message}");
                    return;
                }

                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var result = await _udpClient.ReceiveAsync(_cts.Token);
                        var sender = result.RemoteEndPoint;
                        var data = result.Buffer;

                        if (data.Length > 0)
                        {
                            var messageType = (MessageType)data[0];

                            switch (messageType)
                            {
                                case MessageType.PacketData:
                                    _ = HandlePacketDataShare(sender, data);
                                    break;
                                case MessageType.VoiceData:
                                    _ = HandleVoiceData(sender, data);
                                    break;
                                case MessageType.JoinRoom:
                                    _ = HandleJoinRoom(sender, data);
                                    break;
                                case MessageType.LeaveRoom:
                                    _ = HandleLeaveRoom(sender);
                                    break;
                                case MessageType.JoinChannel:
                                    _ = HandleJoinChannel(sender, data);
                                    break;
                                case MessageType.LeaveChannel:
                                    _ = HandleLeaveChannel(sender);
                                    break;
                                case MessageType.RoomList:
                                    _ = SendRoomList(sender);
                                    break;
                                case MessageType.ChannelList:
                                    _ = SendChannelList(sender, data);
                                    break;
                                default:
                                    _logger.Log($"⚠️ Unknown message type: {messageType} from {sender}");
                                    break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!_cts.IsCancellationRequested)
                        _logger.Log($"❌ UDP server receive loop error: {ex.Message}");
                }
                finally
                {
                    _logger.Log("🛑 UDP server stopped.");
                }
            });
        }

        /// <summary>
        /// Handles voice data packets and forwards them to clients in the same room and channel.
        /// </summary>
        private async Task HandleVoiceData(IPEndPoint sender, byte[] data)
        {
            if (!_clientRooms.TryGetValue(sender, out var roomId) ||
                !_rooms.TryGetValue(roomId, out var room))
            {
                await HandleJoinRoom(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinRoom}lobby"));
                return;
            }

            if (!room.ClientChannels.TryGetValue(sender, out var channelId) ||
                !room.Channels.TryGetValue(channelId, out var channel))
            {
                await HandleJoinChannel(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinChannel}general"));
                return;
            }

            foreach (var clientEndpoint in channel.Clients.Keys)
            {
                if (!clientEndpoint.Equals(sender))
                {
                    try
                    {
                        await _udpClient.SendAsync(data, data.Length, clientEndpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"❌ Failed to send to client {clientEndpoint}. Removing. Error: {ex.Message}");
                        RemoveClient(clientEndpoint);
                    }
                }
            }
        }

        /// <summary>
        /// Handles data packets and forwards them to clients in the same room and channel.
        /// </summary>
        private async Task HandlePacketDataShare(IPEndPoint sender, byte[] data)
        {
            if (!_clientRooms.TryGetValue(sender, out var roomId) ||
                !_rooms.TryGetValue(roomId, out var room))
            {
                await HandleJoinRoom(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinRoom}lobby"));
                return;
            }

            if (!room.ClientChannels.TryGetValue(sender, out var channelId) ||
                !room.Channels.TryGetValue(channelId, out var channel))
            {
                await HandleJoinChannel(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinChannel}general"));
                return;
            }

            if (data.Length > _maxPacketSize)
            {
                _logger.Log($"❌ Failed to handle client data. Removing client {sender}. Max packet size exeeded!");
                RemoveClient(sender);
                return;
            }


            foreach (var clientEndpoint in channel.Clients.Keys)
            {
                if (!clientEndpoint.Equals(sender))
                {
                    try
                    {
                        await _udpClient.SendAsync(data, data.Length, clientEndpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"❌ Failed to send to client {clientEndpoint}. Removing. Error: {ex.Message}");
                        RemoveClient(clientEndpoint);
                    }
                }
            }
        }

        /// <summary>
        /// Handles a request to join a room.
        /// </summary>
        private async Task HandleJoinRoom(IPEndPoint sender, byte[] data)
        {
            try
            {
                var roomId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                if (_clientRooms.TryGetValue(sender, out var currentRoomId))
                {
                    await HandleLeaveRoom(sender);
                }

                var room = _rooms.GetOrAdd(roomId, id => new Room(id, id));
                _clientRooms.TryAdd(sender, roomId);

                await HandleJoinChannel(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinChannel}general"));
                _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} joined room {roomId}");

                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Joined room {roomId}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling join room request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to leave a room.
        /// </summary>
        private async Task HandleLeaveRoom(IPEndPoint sender)
        {
            try
            {
                if (_clientRooms.TryRemove(sender, out var roomId) &&
                    _rooms.TryGetValue(roomId, out var room))
                {
                    await HandleLeaveChannel(sender);

                    _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} left room {roomId}");

                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Left room {roomId}");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling leave room request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to join a channel.
        /// </summary>
        private async Task HandleJoinChannel(IPEndPoint sender, byte[] data)
        {
            try
            {
                if (!_clientRooms.TryGetValue(sender, out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var room))
                {
                    var _response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}You must join a room first");
                    await _udpClient.SendAsync(_response, _response.Length, sender);
                    return;
                }

                var channelId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                if (room.ClientChannels.TryGetValue(sender, out var currentChannelId))
                {
                    await HandleLeaveChannel(sender);
                }

                var channel = room.Channels.GetOrAdd(channelId, id => new Channel { Id = id, Name = id });

                channel.Clients.TryAdd(sender, true);
                room.ClientChannels.TryAdd(sender, channelId);

                _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} joined channel {channelId} in room {roomId}");

                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Joined channel {channelId}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling join channel request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to leave a channel.
        /// </summary>
        private async Task HandleLeaveChannel(IPEndPoint sender)
        {
            try
            {
                if (_clientRooms.TryGetValue(sender, out var roomId) &&
                    _rooms.TryGetValue(roomId, out var room))
                {
                    if (room.ClientChannels.TryRemove(sender, out var channelId) &&
                        room.Channels.TryGetValue(channelId, out var channel))
                    {
                        channel.Clients.TryRemove(sender, out _);

                        _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} left channel {channelId} in room {roomId}");

                        var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Left channel {channelId}");
                        await _udpClient.SendAsync(response, response.Length, sender);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling leave channel request: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a list of available rooms to the client.
        /// </summary>
        private async Task SendRoomList(IPEndPoint sender)
        {
            try
            {
                var roomList = string.Join(",", _rooms.Keys);
                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.RoomList}{roomList}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error sending room list: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a list of available channels in a room to the client.
        /// </summary>
        private async Task SendChannelList(IPEndPoint sender, byte[] data)
        {
            try
            {
                var roomId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                if (_rooms.TryGetValue(roomId, out var room))
                {
                    var channelList = string.Join(",", room.Channels.Keys);
                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ChannelList}{channelList}");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
                else
                {
                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Room {roomId} not found");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error sending channel list: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a client from all rooms and channels.
        /// </summary>
        private void RemoveClient(IPEndPoint clientEndpoint)
        {
            if (_clientRooms.TryRemove(clientEndpoint, out var roomId) &&
                _rooms.TryGetValue(roomId, out var room))
            {
                if (room.ClientChannels.TryRemove(clientEndpoint, out var channelId) &&
                    room.Channels.TryGetValue(channelId, out var channel))
                {
                    channel.Clients.TryRemove(clientEndpoint, out _);
                }
            }
        }

        /// <summary>
        /// Stops the UDP server gracefully.
        /// </summary>
        public async Task StopAsync()
        {
            _cts.Cancel();
            _udpClient?.Close();
            await Task.Delay(100);
        }


        private struct UltimatePacket
        {
            public Vector3 position;
            public Vector3 rotation;
            public Vector3 scale;
            public string state;
            public string extra;
        }

        private struct Vector3
        {
            public int x;
            public int y;
            public int z;
        }
    }
}