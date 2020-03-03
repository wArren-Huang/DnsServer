using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DnsServerCore.Dns.CustomStatistics
{
    public class FixedSizeConcurrentQueue<T> : ConcurrentQueue<T>
    {
        private readonly int MaxSize;
        
        public FixedSizeConcurrentQueue(int maxSize)
        {
            MaxSize = maxSize;
        }

        public new void Enqueue(T item)
        {
            base.Enqueue(item);
            while (Count > MaxSize)
            {
                TryDequeue(out var dequeued);
            }
        }
        
    }
}