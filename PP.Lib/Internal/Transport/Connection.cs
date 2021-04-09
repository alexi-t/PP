using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PP.Lib.Internal.Transport
{
    public class Connection : IDisposable
    {
        private static readonly Dictionary<int, string> _dcs = new()
        {
            { 2, "149.154.167.40" }
        };

        private static Memory<byte> _abrigedHeader = new Memory<byte>(new byte[] { 0xef });

        private readonly Socket _socket;
        private NetworkStream _stream;

        private readonly Channel<Memory<byte>> _readChannel = 
            Channel.CreateUnbounded<Memory<byte>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<Memory<byte>> _writeChannel =
            Channel.CreateUnbounded<Memory<byte>>(new UnboundedChannelOptions { SingleWriter = true });

        private readonly int _dc;

        private readonly ILogger _log;
        

        public Connection(int dc)
        {
            _log = Log.ForContext("SocketDC", _dc);
            _log.Verbose("Start create");

            _dc = dc;
            _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            _log.Verbose("Try open socket");
            _socket.Connect(_dcs[_dc], 443);
            _log.Verbose("Socket opened. Creating stream");
            _stream = new NetworkStream(_socket, ownsSocket: true);

            _log.Verbose("Start message loop");
            _ = Task.Run(ReadLoop);
            _ = Task.Run(WriteLoop);

            Write(_abrigedHeader);

            _log.Verbose("Created");
        }

        public void Dispose()
        {
            _log.Verbose("Disposing");

            _stream.Dispose();
            _socket.Dispose();

            GC.SuppressFinalize(this);
            _log.Verbose("Disposed");
        }

        private async void WriteLoop()
        {
            _log.Verbose("Start write loop");
            await foreach(var message in _writeChannel.Reader.ReadAllAsync())
            {
                if (message.Length == 1 && message.Span[0] == _abrigedHeader.Span[0])
                {
                    await _stream.WriteAsync(message);
                    continue;
                }
                _log.Verbose("Send data length " + message.Length);
                int contentLength = message.Length / 4;
                Memory<byte> header;
                if (contentLength >= 0x7F)
                {
                    header = new byte[]
                    {
                    0x7F,
                    (byte)(contentLength & 0XFF),
                    (byte)((contentLength >> 8) & 0xFF),
                    (byte)((contentLength >> 16) & 0xFF)
                    };
                }
                else
                {
                    header = new byte[] { (byte)contentLength };
                }

                await _stream.WriteAsync(header);
                await _stream.WriteAsync(message);
            }
        }

        private async void ReadLoop()
        {
            _log.Verbose("Start message loop");
            Memory<byte> buffer = new byte[1 << 16];
            for (; ; )
            {
                
                var readedCount = await _stream.ReadAsync(buffer);
                if (readedCount == 0)
                {
                    _log.Verbose("Readed 0 bytes");
                    continue;
                }

                _log.Verbose($"Readed {readedCount} bytes from socket");
                var headerData = buffer.Slice(0, 4).ToArray();
                int messageLength = headerData[0];
                var messageOffset = 1;
                if (messageLength >= 0x7f)
                {
                    messageLength = headerData[1] + (headerData[2] << 8) + (headerData[3] << 16);
                    messageOffset = 4;
                }
                messageLength *= 4;
                _log.Verbose($"Readed message length {messageLength}");

                Memory<byte> messageMemory = new byte[messageLength];

                if (messageLength == 4)
                {
                    _log.Error($"Error from transport {Math.Abs(BitConverter.ToInt32(buffer.Span.Slice(messageOffset, messageLength)))}");
                }
                else if (messageLength > readedCount - messageOffset)
                {
                    buffer[messageOffset..readedCount]
                        .CopyTo(
                            messageMemory.Slice(0, readedCount - messageOffset)
                        );
                    var pointer = readedCount - messageOffset;
                    var leftToRead = messageLength - readedCount - messageOffset;
                    while (leftToRead > 0)
                    {
                        var readed = await _stream.ReadAsync(buffer);
                        buffer
                            .Slice(0, readed)
                            .CopyTo(
                                messageMemory.Slice(pointer, readed)
                            );
                        leftToRead -= readed;
                    }
                }
                else
                    buffer
                        .Slice(messageOffset, messageLength)
                        .CopyTo(messageMemory);

                await _readChannel.Writer.WriteAsync(messageMemory);
            }
        }

        public ValueTask Write(Memory<byte> data) => _writeChannel.Writer.WriteAsync(data);
        public IAsyncEnumerable<Memory<byte>> Read() => _readChannel.Reader.ReadAllAsync();
    }
}
