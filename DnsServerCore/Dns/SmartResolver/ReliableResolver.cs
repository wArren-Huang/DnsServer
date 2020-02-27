using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore.Dns.SmartResolver
{
    public class ReliableResolver
    {
        private const int SPEED_TEST_TIMEOUT = 1000;
        private const int CONCURRENCY = 500;
        private Config Config { get; set; }

        public ReliableResolver(Config config)
        {
            Config = config;
        }
        
        private void WriteLog(string message)
        {
            Config.Log?.Write(message);
        }
        
        public DnsDatagram Resolve(DnsQuestionRecord questionRecord)
        {
            var results = new ConcurrentBag<ResponseResult>();
            string domain = questionRecord.Name;
            var overallStopwatch = new Stopwatch();

            ThreadPool.SetMinThreads(CONCURRENCY, CONCURRENCY);
            ThreadPool.SetMaxThreads(CONCURRENCY, CONCURRENCY);
            
            Console.WriteLine($"ReliableResolver start to resolve [{domain}]");
            overallStopwatch.Start();
            Parallel.ForEach(Config.Forwarders, (forwarder) =>
            {
                var dnsServer = forwarder.Host;
                var resolvingStopwatch = new Stopwatch();
                resolvingStopwatch.Start();
                var response = FastResolver.Resolve(questionRecord, forwarder, Config);
                resolvingStopwatch.Stop();
                Console.WriteLine($"ReliableResolver finish resolving by [{dnsServer}] in {resolvingStopwatch.ElapsedMilliseconds} ms");
                if (response != null)
                { 
                    Parallel.ForEach(response.Answer, (record) =>
                    {
                        if (record.RDLENGTH == "4 bytes")
                        {
                            string ipAddress = record.RDATA.ToString();
                            Parallel.Invoke(
                                () =>
                                {
                                    var speedTestStopwatch = new Stopwatch();
                                    speedTestStopwatch.Start();
                                    var result = HttpsSpeedTester.Test(ipAddress, domain, SPEED_TEST_TIMEOUT);
                                    speedTestStopwatch.Stop();
                                    Console.WriteLine($"ReliableResolver finish HTTPS speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds} ms");
                                    result.DnsServer = dnsServer;
                                    result.DnsResponse = response;
                                    result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                    results.Add(result);
                                },
                                () =>
                                {
                                    var speedTestStopwatch = new Stopwatch();
                                    speedTestStopwatch.Start();
                                    var result = HttpSpeedTester.Test(ipAddress, SPEED_TEST_TIMEOUT);
                                    speedTestStopwatch.Stop();
                                    Console.WriteLine($"ReliableResolver finish HTTP speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds} ms");
                                    result.DnsServer = dnsServer;
                                    result.DnsResponse = response;
                                    result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                    results.Add(result);
                                },
                                () =>
                                {
                                    var speedTestStopwatch = new Stopwatch();
                                    speedTestStopwatch.Start();
                                    var result = PingSpeedTester.Test(ipAddress, SPEED_TEST_TIMEOUT);
                                    speedTestStopwatch.Stop();
                                    Console.WriteLine($"ReliableResolver finish Ping speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds} ms");
                                    result.DnsServer = dnsServer;
                                    result.DnsResponse = response;
                                    result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                    results.Add(result);
                                }
                            );
                        }
                    });
                }
            });
            overallStopwatch.Stop();
            Console.WriteLine($"ReliableResolver finish resolving in {overallStopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine(ResponseResult.PrintableHeader);
            foreach (var result in results)
            {
                Console.WriteLine(result.ToString());
            }
            Console.WriteLine();

            return results.Count > 0 ? results.First().DnsResponse : null;
        }
    }
}