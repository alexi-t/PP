using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PP.Lib.Internal.Transport
{
    public class ConnectionPool
    {
        private readonly int _defaultDC = 2;

        private readonly Dictionary<int, Connection> _sharedConnections = new();
        private readonly Dictionary<int, List<Connection>> _dedicatedConnections = new();

        private readonly ILogger _log = Log.ForContext<ConnectionPool>();

        private readonly Channel<Memory<byte>> _receiveChannel = Channel.CreateUnbounded<Memory<byte>>();

        public ConnectionPool()
        {
            _log.Verbose("Created");
        }

        public async Task Queue(long msgId, Memory<byte> data, int? dc = null, bool dedicated = false)
        {
            _log.Verbose("Queue @msgId", msgId);
            var targetDC = dc ?? _defaultDC;
            if (!_sharedConnections.ContainsKey(targetDC))
            {
                _log.Verbose("Create new connection for @dc", targetDC);
                var newConnection = new Connection(targetDC);
                _sharedConnections.Add(targetDC, newConnection);
                _ = Task.Run(async () =>
                {
                    _log.Verbose("Start read loop for @dc", targetDC);
                    await foreach (var data in newConnection.Read())
                        await _receiveChannel.Writer.WriteAsync(data);
                });
            }

            var connection = _sharedConnections[targetDC];

            await connection.Write(data);
        }

        public IAsyncEnumerable<Memory<byte>> Read() => _receiveChannel.Reader.ReadAllAsync();
        public ValueTask<Memory<byte>> ReadSingle() => _receiveChannel.Reader.ReadAsync();
    }
}
