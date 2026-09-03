using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

// GameClient(콘솔)의 PacketReader/PacketChannel과 같은 4바이트 헤더([2B size][2B id]) 프레이밍을
// 그대로 재구현. Unity는 async/await로 응답을 기다리다 Unity API를 건드리면 스레드 문제가 나므로,
// 여기선 백그라운드 스레드가 읽은 패킷을 큐에 쌓아두고 GameDemo.Update()(메인 스레드)가 매 프레임
// 꺼내 처리하는 방식으로 마샬링한다.
public sealed class NetworkClient
{
    public readonly ConcurrentQueue<(ushort Id, byte[] Payload)> Incoming = new();

    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _readThread;
    private volatile bool _running;

    public bool IsConnected => _client != null && _client.Connected && _running;
    public string LastError { get; private set; }

    public bool Connect(string host, int port)
    {
        try
        {
            _client = new TcpClient();
            _client.Connect(host, port);
            _stream = _client.GetStream();
            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public void Send(ushort id, byte[] payload = null)
    {
        payload ??= Array.Empty<byte>();
        int size = 4 + payload.Length;
        var buf = new byte[size];
        BitConverter.GetBytes((ushort)size).CopyTo(buf, 0);
        BitConverter.GetBytes(id).CopyTo(buf, 2);
        payload.CopyTo(buf, 4);
        _stream.Write(buf, 0, buf.Length);
    }

    private void ReadLoop()
    {
        try
        {
            var header = new byte[4];
            while (_running)
            {
                ReadExact(header, 4);
                ushort size = BitConverter.ToUInt16(header, 0);
                ushort id = BitConverter.ToUInt16(header, 2);
                int payloadLen = size - 4;
                var payload = payloadLen > 0 ? new byte[payloadLen] : Array.Empty<byte>();
                if (payloadLen > 0) ReadExact(payload, payloadLen);
                Incoming.Enqueue((id, payload));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    private void ReadExact(byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = _stream.Read(buf, offset, count - offset);
            if (read == 0) throw new IOException("connection closed");
            offset += read;
        }
    }

    public void Close()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
