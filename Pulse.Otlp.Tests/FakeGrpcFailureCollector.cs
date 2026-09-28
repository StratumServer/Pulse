using System.Net;
using System.Net.Sockets;

namespace Pulse.Otlp.Tests;

/// <summary>Just enough of an OTLP/gRPC collector to answer one export with a non-OK gRPC status:
/// settings, no data back, then a trailer naming status 16 (UNAUTHENTICATED). Adapted from
/// Pulse.Otlp.Scenarios/FakeGrpcCollector.cs, which answers OK instead; see that file's remarks
/// for why this is hand-rolled rather than a real HTTP/2 server.</summary>
internal sealed class FakeGrpcFailureCollector : IDisposable
{
    private const int PrefaceLength = 24;

    private const byte Data = 0x00;
    private const byte Headers = 0x01;
    private const byte Settings = 0x04;
    private const byte GoAway = 0x07;
    private const byte EndStream = 0x01;
    private const byte EndHeaders = 0x04;
    private const byte Ack = 0x01;

    private static readonly byte[] ServerSettings = [0, 0, 0, Settings, 0, 0, 0, 0, 0];
    private static readonly byte[] SettingsAck = [0, 0, 0, Settings, Ack, 0, 0, 0, 0];

    /// <summary>":status: 200" is entry 8 of HPACK's static table, so it travels as one indexed
    /// byte; the gRPC failure travels in the trailer below, not in this HTTP status.</summary>
    private static readonly byte[] ResponseHeaders =
        [0x88, 0x0f, 0x10, 0x10, .. "application/grpc"u8];

    private static readonly byte[] EmptyResponseMessage = [0, 0, 0, 0, 0];

    /// <summary>"grpc-status: 16" and "grpc-message: invalid token" as trailer literals with new
    /// names. 16 is UNAUTHENTICATED; a real status is what proves the exporter's own status
    /// parsing, rather than only its network error handling.</summary>
    private static readonly byte[] UnauthenticatedTrailer =
    [
        0x00, 0x0b, .. "grpc-status"u8, 0x02, .. "16"u8,
        0x00, 0x0c, .. "grpc-message"u8, 0x0d, .. "invalid token"u8,
    ];

    private readonly TcpListener listener;
    private readonly CancellationTokenSource closing = new();

    public FakeGrpcFailureCollector(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Task.Run(Accept);
    }

    public void Dispose()
    {
        closing.Cancel();
        listener.Stop();
        closing.Dispose();
    }

    private async Task Accept()
    {
        CancellationToken token = closing.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Serve(client, token), CancellationToken.None);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();

                // The server's SETTINGS frame opens the conversation: a client using HTTP/2 with
                // prior knowledge, which is what cleartext gRPC is, will not send its request
                // until that frame arrives.
                await stream.WriteAsync(ServerSettings, token);
                await stream.FlushAsync(token);

                byte[] preface = new byte[PrefaceLength];
                await stream.ReadExactlyAsync(preface, token);

                await Pump(stream, token);
            }
            catch (Exception)
            {
                // A collector losing a connection is not a test failure: the test asserts on what
                // the exporter itself logged, not on what this collector observed.
            }
        }
    }

    private static async Task Pump(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[9];
        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, token);
            int length = (header[0] << 16) | (header[1] << 8) | header[2];
            byte type = header[3];
            byte flags = header[4];
            int streamId = ((header[5] & 0x7f) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

            byte[] payload = new byte[length];
            if (length > 0)
            {
                await stream.ReadExactlyAsync(payload, token);
            }

            switch (type)
            {
                case Settings when (flags & Ack) == 0:
                    await stream.WriteAsync(SettingsAck, token);
                    await stream.FlushAsync(token);
                    break;

                // An export small enough to fit one frame is the only shape the exporter sends
                // here, so the data frame that carries it also ends the stream.
                case Data when (flags & EndStream) == EndStream:
                    await Respond(stream, streamId, token);
                    break;

                case GoAway:
                    return;
            }
        }
    }

    private static async Task Respond(NetworkStream stream, int streamId, CancellationToken token)
    {
        byte[] response =
        [
            .. Frame(Headers, EndHeaders, streamId, ResponseHeaders),
            .. Frame(Data, 0, streamId, EmptyResponseMessage),
            .. Frame(Headers, EndHeaders | EndStream, streamId, UnauthenticatedTrailer),
        ];

        await stream.WriteAsync(response, token);
        await stream.FlushAsync(token);
    }

    private static byte[] Frame(byte type, byte flags, int streamId, byte[] payload) =>
    [
        (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
        type,
        flags,
        (byte)(streamId >> 24), (byte)(streamId >> 16), (byte)(streamId >> 8), (byte)streamId,
        .. payload,
    ];
}
