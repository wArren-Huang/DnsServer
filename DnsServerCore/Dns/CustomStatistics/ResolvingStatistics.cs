using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DnsServerCore.Dns.AntiRogue;

namespace DnsServerCore.Dns.CustomStatistics
{
    public static class ResolvingStatistics
    {
        private const int ParallelResolvingCount = 200;
        private const int ClientsCount = 100;
        public const int LastResolvingAttemptsSizeLimit = 50;

        private const int DomainsCount = AntiRogueTester.ExpectedRogueCount + 
                                         AntiRogueTester.ExpectedNonRogueCount + 
                                         AntiRogueTester.ExpectedCannotDetermineCount + 
                                         AntiRogueTester.ParallelTestingsCount;

        //<client, domains>
        private static ConcurrentDictionary<string, FixedSizeConcurrentQueue<string>> ByClientLastResolvingAttempts;
        private static ConcurrentDictionary<string, SignificanceCounter<string>> ByClientDomainResolvingCounter;
        private static SignificanceCounter<string> GlobalDomainResolvingCounter;
        
        static ResolvingStatistics()
        {
            ByClientLastResolvingAttempts = new ConcurrentDictionary<string, FixedSizeConcurrentQueue<string>>(ParallelResolvingCount, ClientsCount);
            ByClientDomainResolvingCounter = new ConcurrentDictionary<string, SignificanceCounter<string>>(ParallelResolvingCount, ClientsCount);
            GlobalDomainResolvingCounter = new SignificanceCounter<string>(ParallelResolvingCount, DomainsCount);
        }

        public static void Encountered(string client, string domain)
        {
            if (!ByClientLastResolvingAttempts.ContainsKey(client))
            {
                ByClientLastResolvingAttempts[client] = new FixedSizeConcurrentQueue<string>(LastResolvingAttemptsSizeLimit);
            }
            if (!ByClientDomainResolvingCounter.ContainsKey(client))
            {
                ByClientDomainResolvingCounter[client] = new SignificanceCounter<string>(ParallelResolvingCount, DomainsCount);
            }
            ByClientLastResolvingAttempts[client].Enqueue(domain);
            ByClientDomainResolvingCounter[client].Encountered(domain);
            GlobalDomainResolvingCounter.Encountered(domain);
        }
        
        public static IEnumerable<KeyValuePair<string, int>> GetGlobalMostSignificantQuestions(int count)
        {
            return GlobalDomainResolvingCounter.GetMostSignificantItems(count);
        }
        
        public static IEnumerable<string> GetClients()
        {
            return ByClientLastResolvingAttempts.Keys;
        }

        public static IEnumerable<KeyValuePair<string, int>> GetClientMostSignificantQuestions(string client, int count)
        {
            if (ByClientDomainResolvingCounter.ContainsKey(client))
            {
                return ByClientDomainResolvingCounter[client].GetMostSignificantItems(count);
            }
            return new KeyValuePair<string, int>[0] ;
        }
        
        public static IEnumerable<string> GetClientLastResolvingAttempts(string client)
        {
            if (ByClientLastResolvingAttempts.ContainsKey(client))
            {
                return ByClientLastResolvingAttempts[client];
            }
            return new string[0] ;
        }
    }
}