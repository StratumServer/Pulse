using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pulse.Otlp.Scenarios;

/// <summary>An OTLP/gRPC collector reduced to what a test needs: it speaks just enough HTTP/2 to
/// receive exports and answer them the way a collector answers, and it keeps the first one
/// whole.</summary>
/// <remarks>Hand-rolled rather than Kestrel because gRPC over cleartext needs an HTTP/2 server and
/// nothing else here does: taking the ASP.NET Core shared framework as a test dependency would put
/// a second runtime on the list of things a contributor has to install to run the suite. What the
/// exporter needs from a server is small enough to write out: settings, one response, trailers.
/// The request headers are left HPACK-encoded and matched as bytes, which works because the
/// exporter's HttpClient writes literal header values uncompressed. Were that ever to change, the
/// scenario's path assertion fails loudly and this grows an HPACK decoder.</remarks>
internal sealed class FakeGrpcCollector : IDisposable
{
    /// <summary>The path a metrics export is addressed to, which is the gRPC service and method
    /// names from the OTLP protobuf definition.</summary>
    public const string ExportPath = "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export";

    private const int PrefaceLength = 24;

    private const byte Data = 0x00;
    private const byte Headers = 0x01;
    private const byte Settings = 0x04;
    private const byte GoAway = 0x07;
    private const byte EndStream = 0x01;
    private const byte EndHeaders = 0x04;
    private const byte Ack = 0x01;

    /// <summary>An empty SETTINGS frame, and the acknowledgement of the client's own.</summary>
    private static readonly byte[] ServerSettings = [0, 0, 0, Settings, 0, 0, 0, 0, 0];
    private static readonly byte[] SettingsAck = [0, 0, 0, Settings, Ack, 0, 0, 0, 0];

    /// <summary>":status: 200" is entry 8 of HPACK's static table, so it travels as one indexed
    /// byte. "content-type" is entry 31, so it is named by index and given a literal value.</summary>
    private static readonly byte[] ResponseHeaders =
        [0x88, 0x0f, 0x10, 0x10, .. "application/grpc"u8];

    /// <summary>One gRPC message: not compressed, and four length bytes of zero. An
    /// ExportMetricsServiceResponse with nothing set is zero bytes long.</summary>
    private static readonly byte[] EmptyResponseMessage = [0, 0, 0, 0, 0];

    /// <summary>"grpc-status: 0" as a literal with a new name. gRPC carries its real status in a
    /// trailer, and zero is OK; a client that never sees it treats the call as failed.</summary>
    private static readonly byte[] StatusOkTrailer =
        [0x00, 0x0b, .. "grpc-status"u8, 0x01, .. "0"u8];

    private readonly TcpListener listener;
    private readonly CancellationTokenSource closing = new();

    /// <summary>The first export received, or null while none has arrived. Written by the listener
    /// thread and read by the scenario, hence the volatile: the record itself is immutable.</summary>
    private volatile Export? first;

    public FakeGrpcCollector(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Task.Run(Accept);
    }

    public Export? First => first;

    public void Dispose()
    {
        // Cancelling first unblocks the accept and the frame reads, which are otherwise parked on
        // a socket nobody is going to write to again.
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
                // A collector losing a connection is not a test failure. The scenario asserts on
                // what arrived, and an export that never arrives fails there with a better message
                // than an exception on a background thread would give.
            }
        }
    }

    /// <summary>Reads frames until the client goes away, pairing each request's header block with
    /// its body. The exporter has one export in flight at a time, so tracking a single header block
    /// rather than one per stream is enough.</summary>
    private async Task Pump(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[9];
        byte[] headerBlock = [];

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

                case Headers:
                    headerBlock = payload;
                    break;

                case Data:
                    // An export small enough to fit one frame is the only shape the exporter sends
                    // here, so the body is whatever came with the frame that ends the stream.
                    if (length > 0)
                    {
                        first ??= new Export(headerBlock, payload);
                    }

                    if ((flags & EndStream) == EndStream)
                    {
                        await Respond(stream, streamId, token);
                    }

                    break;

                case GoAway:
                    return;
            }
        }
    }

    private async Task Respond(NetworkStream stream, int streamId, CancellationToken token)
    {
        byte[] response =
        [
            .. Frame(Headers, EndHeaders, streamId, ResponseHeaders),
            .. Frame(Data, 0, streamId, EmptyResponseMessage),
            .. Frame(Headers, EndHeaders | EndStream, streamId, StatusOkTrailer),
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

    /// <summary>One export: the request's HPACK header block as it arrived, and the gRPC message
    /// that followed it.</summary>
    internal sealed record Export(byte[] HeaderBlock, byte[] Body)
    {
        /// <summary>The header block read as text. Names and values the exporter sends are written
        /// as uncompressed literals, so they read straight out of the bytes; the table indices
        /// around them do not, which is why this is only ever searched, never parsed.</summary>
        /// <remarks>Latin-1 rather than UTF-8 because it maps every byte to exactly one character.
        /// A stray index byte in the block would otherwise be able to read as the start of a
        /// multi-byte sequence and swallow the header name printed right after it.</remarks>
        public string Headers => Encoding.Latin1.GetString(HeaderBlock);
    }
}
