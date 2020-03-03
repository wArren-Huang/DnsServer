using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DnsServerCore.Dns.CustomStatistics
{
    public class SignificanceCounter<T>
    {
        private readonly ConcurrentDictionary<T, int> ItemsOccurrenceCount;

        public SignificanceCounter(int concurrency, int capacity)
        {
            ItemsOccurrenceCount = new ConcurrentDictionary<T, int>(concurrency, capacity);
        }
        
        public SignificanceCounter(IEnumerable<KeyValuePair<T,int>> initialItems, int concurrency, int capacity)
        {
            ItemsOccurrenceCount = new ConcurrentDictionary<T, int>(concurrency, capacity);
            foreach (var item in initialItems)
            {
                ItemsOccurrenceCount[item.Key] = item.Value;
            }
        }

        public void Encountered(T item)
        {
            if (ItemsOccurrenceCount.ContainsKey(item))
            {
                ItemsOccurrenceCount[item]++;
            }
            else
            {
                ItemsOccurrenceCount[item] = 1;
            }
        }

        public IEnumerable<KeyValuePair<T, int>> GetMostSignificantItems(int count)
        {
            return ItemsOccurrenceCount.OrderByDescending(kvp => kvp.Value).Take(count);
        }
    }
}