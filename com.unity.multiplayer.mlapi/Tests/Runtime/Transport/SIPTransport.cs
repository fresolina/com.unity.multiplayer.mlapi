using System;
using System.Collections.Generic;
using MLAPI.Editor;
using MLAPI.Transports;
using MLAPI.Transports.Tasks;
using UnityEngine;

/// <summary>
/// SIPTransport (SIngleProcessTransport)
/// is a NetworkTransport designed to be used with multiple MLAPI instances in a single process
/// it's designed for the MLAPI in a way where no networking stack has to be available
/// it's designed for testing purposes and it's not designed with speed in mind
/// </summary>
[DontShowInTransportDropdown]
public class SIPTransport : NetworkTransport
{
    private struct Event
    {
        public NetworkEvent Type;
        public ulong ConnectionId;
        public ArraySegment<byte> Data;
        public NetworkChannel Channel;
    }

    private class Peer
    {
        public ulong ConnectionId;
        public SIPTransport Transport;
        public Queue<Event> IncomingBuffer = new Queue<Event>();
    }

    private readonly Dictionary<ulong, Peer> m_Peers = new Dictionary<ulong, Peer>();
    private ulong m_ClientsCounter = 1;

    private static Peer s_Server;
    private Peer m_LocalConnection;

    public override ulong ServerClientId => 0;
    public ulong LocalClientId;

    public override void DisconnectLocalClient()
    {
        if (m_LocalConnection != null)
        {
            // Inject local disconnect
            m_LocalConnection.IncomingBuffer.Enqueue(new Event
            {
                Type = NetworkEvent.Disconnect,
                Channel = NetworkChannel.Internal,
                ConnectionId = m_LocalConnection.ConnectionId,
                Data = new ArraySegment<byte>()
            });

            if (s_Server != null && m_LocalConnection != null)
            {
                // Remove the connection
                s_Server.Transport.m_Peers.Remove(m_LocalConnection.ConnectionId);
            }

            if (m_LocalConnection.ConnectionId == ServerClientId)
            {
                StopServer();
            }

            // Remove the local connection
            m_LocalConnection = null;
        }
    }

    // Called by server
    public override void DisconnectRemoteClient(ulong clientId)
    {
        if (m_Peers.ContainsKey(clientId))
        {
            // Inject disconnect into remote
            m_Peers[clientId].IncomingBuffer.Enqueue(new Event
            {
                Type = NetworkEvent.Disconnect,
                Channel = NetworkChannel.Internal,
                ConnectionId = clientId,
                Data = new ArraySegment<byte>()
            });

            // Inject local disconnect
            m_LocalConnection.IncomingBuffer.Enqueue(new Event
            {
                Type = NetworkEvent.Disconnect,
                Channel = NetworkChannel.Internal,
                ConnectionId = clientId,
                Data = new ArraySegment<byte>()
            });

            // Remove the local connection on remote
            m_Peers[clientId].Transport.m_LocalConnection = null;

            // Remove connection on server
            m_Peers.Remove(clientId);
        }
    }

    public override ulong GetCurrentRtt(ulong clientId)
    {
        // Always returns 50ms
        return 50;
    }

    public override void Init()
    {
    }

    private void StopServer()
    {
        s_Server = null;
        m_Peers.Remove(ServerClientId);
    }

    public override void Shutdown()
    {
        // Inject disconnects to all the remotes
        foreach (KeyValuePair<ulong, Peer> onePeer in m_Peers)
        {
            onePeer.Value.IncomingBuffer.Enqueue(new Event
            {
                Type = NetworkEvent.Disconnect,
                Channel = NetworkChannel.Internal,
                ConnectionId = LocalClientId,
                Data = new ArraySegment<byte>()
            });
        }

        if (m_LocalConnection != null && m_LocalConnection.ConnectionId == ServerClientId)
        {
            StopServer();
        }


        // TODO: Cleanup
    }

    public override SocketTasks StartClient()
    {
        if (s_Server == null)
        {
            // No server
            Debug.LogError("No server");
            return SocketTask.Fault.AsTasks();
        }

        if (m_LocalConnection != null)
        {
            // Already connected
            Debug.LogError("Already connected");
            return SocketTask.Fault.AsTasks();
        }

        // Generate an Id for the server that represents this client
        ulong serverConnectionId = ++s_Server.Transport.m_ClientsCounter;
        LocalClientId = serverConnectionId;

        // Create local connection
        m_LocalConnection = new Peer()
        {
            ConnectionId = serverConnectionId,
            Transport = this,
            IncomingBuffer = new Queue<Event>()
        };

        // Add the server as a local connection
        m_Peers.Add(ServerClientId, s_Server);

        // Add local connection as a connection on the server
        s_Server.Transport.m_Peers.Add(serverConnectionId, m_LocalConnection);

        // Sends a connect message to the server
        s_Server.Transport.m_LocalConnection.IncomingBuffer.Enqueue(new Event()
        {
            Type = NetworkEvent.Connect,
            ConnectionId = serverConnectionId,
            Data = new ArraySegment<byte>()
        });

        // Send a local connect message
        m_LocalConnection.IncomingBuffer.Enqueue(new Event
        {
            Type = NetworkEvent.Connect,
            ConnectionId = ServerClientId,
            Data = new ArraySegment<byte>()
        });

        return SocketTask.Done.AsTasks();
    }

    public override SocketTasks StartServer()
    {
        if (s_Server != null)
        {
            // Can only have one server
            Debug.LogError("Server already started");
            return SocketTask.Fault.AsTasks();
        }

        if (m_LocalConnection != null)
        {
            // Already connected
            Debug.LogError("Already connected");
            return SocketTask.Fault.AsTasks();
        }

        // Create local connection
        m_LocalConnection = new Peer()
        {
            ConnectionId = ServerClientId,
            Transport = this,
            IncomingBuffer = new Queue<Event>()
        };

        // Set the local connection as the server
        s_Server = m_LocalConnection;

        m_Peers.Add(ServerClientId, s_Server);

        return SocketTask.Done.AsTasks();
    }

    public override void Send(ulong clientId, ArraySegment<byte> data, NetworkChannel channel)
    {
        if (m_LocalConnection != null)
        {
            // Create copy since MLAPI wants the byte array back straight after the method call.
            // Hard on GC.
            byte[] copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);

            if (!m_Peers.ContainsKey(clientId))
            {
                throw new KeyNotFoundException($"peer id {clientId} not in peer list");
            }

            m_Peers[clientId].IncomingBuffer.Enqueue(new Event
            {
                Type = NetworkEvent.Data,
                ConnectionId = m_LocalConnection.ConnectionId,
                Data = new ArraySegment<byte>(copy),
                Channel = channel
            });
        }
    }

    public override NetworkEvent PollEvent(out ulong clientId, out NetworkChannel channel, out ArraySegment<byte> payload, out float receiveTime)
    {
        if (m_LocalConnection != null)
        {
            if (m_LocalConnection.IncomingBuffer.Count == 0)
            {
                clientId = 0;
                channel = NetworkChannel.Internal;
                payload = new ArraySegment<byte>();
                receiveTime = 0;
                return NetworkEvent.Nothing;
            }

            var peerEvent = m_LocalConnection.IncomingBuffer.Dequeue();

            clientId = peerEvent.ConnectionId;
            channel = peerEvent.Channel;
            payload = peerEvent.Data;
            receiveTime = 0;

            return peerEvent.Type;
        }
        else
        {
            clientId = 0;
            channel = NetworkChannel.Internal;
            payload = new ArraySegment<byte>();
            receiveTime = 0;
            return NetworkEvent.Nothing;
        }
    }
}
