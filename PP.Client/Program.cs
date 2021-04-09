using PP.Lib.Internal.Auth;
using PP.Lib.Internal.Transport;
using Serilog;
using System;
using System.Diagnostics.Tracing;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PP.Client
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            var pool = new ConnectionPool();
            var gen = new AuthKeyGenerator(pool);

            var (key, salt) =await gen.GetKey();

            
        }
    }
}
