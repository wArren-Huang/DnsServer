using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.SmartResolver
{
    public static class ResolvingPreference
    {
        private const int ShortenedDomainLevels = 3;
        private const int Concurrency = 500;
        private const int TargetDomainCount = 500;
        private const int DnsServerCount = 10;
        private const string DomainSplitterString = ".";
        private const SpeedTestMethod DefaultSpeedTestMethod = SpeedTestMethod.Unspecified;
        
        private static readonly char[] DomainSplitterCharArray;
        private static readonly object TimeLock;
        
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> RougeServers;
        private static readonly ConcurrentDictionary<string, DateTime> SlowServers;
        private static readonly ConcurrentDictionary<string, DateTime> KnownDomains;
        private static readonly ConcurrentDictionary<string, PreferMethodRecord> DomainPreferredMethod;

        private static readonly TimeSpan BlockRougeServerFor;
        private static readonly TimeSpan BlockSlowServerFor;
        private static readonly TimeSpan ExpirePreferMethodAfter;
        private static readonly TimeSpan ForgetDomainAfter;
        
        private static DateTime LastUpdate;
        private static DateTime LastSave;

        static ResolvingPreference()
        {
            DomainSplitterCharArray = new[] {'.'};
            TimeLock = new object();
            
            RougeServers = 
                new ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>>(Concurrency, TargetDomainCount);
            SlowServers = 
                new ConcurrentDictionary<string, DateTime>(Concurrency, DnsServerCount);
            DomainPreferredMethod = 
                new ConcurrentDictionary<string, PreferMethodRecord>(Concurrency, TargetDomainCount);
            KnownDomains = 
                new ConcurrentDictionary<string, DateTime>();

            //TODO read from file
            BlockRougeServerFor = TimeSpan.FromMinutes(4);
            BlockSlowServerFor = TimeSpan.FromMinutes(2);
            ExpirePreferMethodAfter = TimeSpan.FromSeconds(65);
            ForgetDomainAfter = TimeSpan.FromMinutes(3);
            lock (TimeLock)
            {
                LastUpdate = DateTime.Now;
                LastSave = DateTime.Now.AddMilliseconds(1);
            }
        }

        private static void MarkUpdated()
        {
            lock (TimeLock)
            {
                LastUpdate = DateTime.Now;
            }
        }

        private static bool NotExpired(DateTime startTime, TimeSpan timeSpan)
        {
            return DateTime.Now.CompareTo(startTime.Add(timeSpan)) < 0;
        }
        
        public static bool IsKnown(string fqdn)
        {
            if (KnownDomains.ContainsKey(fqdn))
            {
                return NotExpired(KnownDomains[fqdn], ForgetDomainAfter);
            }
            KnownDomains[fqdn] = DateTime.Now;
            MarkUpdated();
            Console.WriteLine(
                $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Domain [{fqdn}] is now known " +
                $"since [{KnownDomains[fqdn].ToString(CultureInfo.CurrentCulture)}] " +
                $"for [{ForgetDomainAfter.ToString()}] " +
                $"until [{KnownDomains[fqdn].Add(ForgetDomainAfter).ToString(CultureInfo.CurrentCulture)}]");
            return false;
        }

        public static SpeedTestMethod GetPreferredMethod(string fqdn)
        {
            if (!DomainPreferredMethod.ContainsKey(fqdn))
            {
                return DefaultSpeedTestMethod;
            }

            var preferStartTime = DomainPreferredMethod[fqdn].StartTime;
            if (NotExpired(preferStartTime, ExpirePreferMethodAfter))
            {
                return DomainPreferredMethod[fqdn].Method;
            }
            DomainPreferredMethod.TryRemove(fqdn, out var expiredRecord);
            MarkUpdated();
            Console.WriteLine(
                $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Removed preferred method [{expiredRecord.Method.ToString()}] for domain [{fqdn}] " +
                $"which expired at [{preferStartTime.Add(ExpirePreferMethodAfter).ToString(CultureInfo.CurrentCulture)}]");
            return DefaultSpeedTestMethod;
        }

        public static void SetPreferredMethod(string fqdn, SpeedTestMethod method)
        {
            if (DomainPreferredMethod.ContainsKey(fqdn))
            {
                lock (DomainPreferredMethod[fqdn])
                {
                    DomainPreferredMethod[fqdn].Method = method;
                    DomainPreferredMethod[fqdn].StartTime = DateTime.Now;
                }
            }
            else
            {
                DomainPreferredMethod[fqdn] = new PreferMethodRecord()
                {
                    Method = method,
                    StartTime = DateTime.Now
                };
            }
            MarkUpdated();
            Console.WriteLine(
                $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Set prefer [{method.ToString()}] as speed test method for [{fqdn}] " +
                $"since [{DomainPreferredMethod[fqdn].StartTime.ToString(CultureInfo.CurrentCulture)}] " +
                $"for [{ExpirePreferMethodAfter.ToString()}] " +
                $"until [{DomainPreferredMethod[fqdn].StartTime.Add(ExpirePreferMethodAfter).ToString(CultureInfo.CurrentCulture)}]");
        }

        public static void MarkAsBlockedForBeingRouge(string fqdn, string dnsServer)
        {
            var shortenedDomain = GetShortenedDomain(fqdn);
            var blockEntries =
                RougeServers.GetOrAdd(shortenedDomain, sd => 
                    new ConcurrentDictionary<string, DateTime>(Concurrency, DnsServerCount));
            blockEntries[dnsServer] = DateTime.Now;
            MarkUpdated();
            Console.WriteLine(
                $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Marked [{dnsServer}] as rouge for shortened domain [{shortenedDomain}] " +
                $"since [{RougeServers[shortenedDomain][dnsServer].ToString(CultureInfo.CurrentCulture)}] " +
                $"for [{BlockRougeServerFor.ToString()}] " +
                $"expires at [{RougeServers[shortenedDomain][dnsServer].Add(BlockRougeServerFor).ToString(CultureInfo.CurrentCulture)}]");
        }
        
        public static bool IsBlockedForBeingRouge(string fqdn, NameServerAddress dnsServer)
        {
            var shortenedDomain = GetShortenedDomain(fqdn);
            var dnsServerString = dnsServer.ToString();

            if (string.IsNullOrEmpty(dnsServerString))
            {
                return false;
            }

            if (!RougeServers.ContainsKey(shortenedDomain))
            {
                return false;
            }

            foreach (var rougeRecord in RougeServers[shortenedDomain])
            {
                if (NotExpired(rougeRecord.Value, BlockRougeServerFor))
                {
                    continue;
                }
                RougeServers[shortenedDomain].TryRemove(rougeRecord.Key, out var expiredRecord);
                MarkUpdated();
                Console.WriteLine(
                    $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Removed [{rougeRecord.Key}] from rouge blocked list for shortened domain [{shortenedDomain}] " +
                    $"which expired at [{expiredRecord.Add(BlockRougeServerFor).ToString(CultureInfo.CurrentCulture)}]");
            }

            return RougeServers[shortenedDomain].Any(record => 
                dnsServerString.Equals(record.Key) && 
                NotExpired(record.Value, BlockRougeServerFor));
        }

        public static void MarkAsBlockedForBeingSlow(string dnsServer)
        {
            SlowServers[dnsServer] = DateTime.Now;
            MarkUpdated();
            Console.WriteLine(
                $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Marked [{dnsServer}] as slow " +
                $"since [{SlowServers[dnsServer].ToString(CultureInfo.CurrentCulture)}] " +
                $"for [{BlockSlowServerFor.ToString()}] " +
                $"expires at [{SlowServers[dnsServer].Add(BlockSlowServerFor).ToString(CultureInfo.CurrentCulture)}]");
        }

        public static bool IsBlockedForBeingSlow(NameServerAddress dnsServer)
        {
            var dnsServerString = dnsServer.ToString();
            if (string.IsNullOrEmpty(dnsServerString))
            {
                return false;
            }

            if (!SlowServers.ContainsKey(dnsServerString))
            {
                return false;
            }

            var expired = !NotExpired(SlowServers[dnsServerString], BlockSlowServerFor);
            if (expired)
            {
                SlowServers.TryRemove(dnsServerString, out var expiredRecord);
                Console.WriteLine(
                    $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Removed [{dnsServerString}] from slow blocked list " +
                    $"which expired at [{expiredRecord.Add(BlockSlowServerFor).ToString(CultureInfo.CurrentCulture)}]");
                MarkUpdated();
            }
            return ! expired;
        }

        private static string GetShortenedDomain(string domain)
        {
            if (string.IsNullOrEmpty(domain))
            {
                return domain;
            }

            var domainLevels = domain.Split(DomainSplitterCharArray, StringSplitOptions.RemoveEmptyEntries);
            if (domainLevels.Length <= ShortenedDomainLevels)
            {
                return domain;
            }

            var matchedDomainLevels = domain
                .Skip(domainLevels.Length - ShortenedDomainLevels)
                .Take(ShortenedDomainLevels)
                .ToArray();
            return string.Join(DomainSplitterString, matchedDomainLevels);
        }

        private class PreferMethodRecord
        {
            public SpeedTestMethod Method { get; set; }
            public DateTime StartTime { get; set; }
        }
    }
}