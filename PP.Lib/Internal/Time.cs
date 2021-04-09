using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PP.Lib.Internal
{
    public static class Time
    {
        private readonly static object _lock = new object();

        private static long _lastId = 0;
        private static long _offset = 0;
        public static void CorrectOffset(long timeFromServer)
        {
            var nowTime = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            var serverTime = timeFromServer >> 32;
            _offset = serverTime - nowTime;
        }
        public static long GetId()
        {
            lock (_lock)
            {
                var id = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + _offset) << 32;
                if (id <= _lastId)
                    id = _lastId + 2;
                _lastId = id;
                return _lastId;
            }            
        }
    }
}
