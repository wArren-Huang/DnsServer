using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore.Dns.SmartResolver
{
    public class FastResolver
    {
        private readonly ManualResetEvent _ManualResetEvent = new ManualResetEvent(false);
        private readonly Response _Response = new Response();
        
        public static DnsDatagram Resolve(DnsQuestionRecord questionRecord, NameServerAddress dnsServer,
            DnsResolverConfig dnsResolverConfig)
        {
            try
            {
                var dnsClient = new DnsClient(dnsServer)
                {
                    Proxy = dnsResolverConfig.Proxy,
                    PreferIPv6 = dnsResolverConfig.PreferIPv6,
                    Protocol = dnsResolverConfig.ForwarderProtocol,
                    Retries = dnsResolverConfig.Retries,
                    Timeout = dnsResolverConfig.Timeout
                };
                return dnsClient.Resolve(questionRecord);
            }
            catch
            {
                return null;
            }
        }

        public static DnsDatagram Resolve(DnsQuestionRecord questionRecord, DnsResolverConfig dnsResolverConfig)
        {
            var fastResolver = new FastResolver();
            return fastResolver.ResolveAndReturnFirstResponse(questionRecord, dnsResolverConfig.Forwarders, dnsResolverConfig);
        }

        private DnsDatagram ResolveAndReturnFirstResponse(DnsQuestionRecord questionRecord, 
            NameServerAddress[] dnsServers, DnsResolverConfig dnsResolverConfig)
        {
            var question = questionRecord.Name;
            var overallStopwatch = new Stopwatch();

            overallStopwatch.Start();
            Console.WriteLine($"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] start to resolve [{question}]");
            foreach (var dnsServer in dnsServers)
            {
                var context = new ResolvingContext()
                {
                    DnsResolverConfig = dnsResolverConfig,
                    DnsServer = dnsServer,
                    QuestionRecord = questionRecord
                } as object;
                ThreadPool.QueueUserWorkItem(ResolveWith, context);
            }
            _ManualResetEvent.WaitOne();
            lock (_Response)
            {
                overallStopwatch.Stop();
                Console.WriteLine(
                    $"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] finish resolving [{question}] by [{_Response.DnsServer}] in {overallStopwatch.ElapsedMilliseconds} ms");
                return _Response.DnsResponse;
            }
        }

        private void ResolveWith(object context)
        {
            if (context is ResolvingContext resolvingContext)
            {
                Console.WriteLine(
                    $"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] start resolving [{resolvingContext.QuestionRecord.Name}] from [{resolvingContext.DnsServer}]");
                var stopWatch = new Stopwatch();
                stopWatch.Start();
                try
                {
                    var dnsClient = new DnsClient(resolvingContext.DnsServer)
                    {
                        Proxy = resolvingContext.DnsResolverConfig.Proxy,
                        PreferIPv6 = resolvingContext.DnsResolverConfig.PreferIPv6,
                        Protocol = resolvingContext.DnsResolverConfig.ForwarderProtocol,
                        Retries = resolvingContext.DnsResolverConfig.Retries,
                        Timeout = resolvingContext.DnsResolverConfig.Timeout
                    };
                    var response = dnsClient.Resolve(resolvingContext.QuestionRecord);
                    if (response != null)
                    {
                        lock (this._Response)
                        {
                            this._Response.DnsResponse = response;
                            this._Response.DnsServer = resolvingContext.DnsServer;
                        }
                        _ManualResetEvent.Set();
                    }
                }
                catch (DnsClientException)
                {
                    stopWatch.Stop();
                    Console.WriteLine(
                        $"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] failed to resolve [{resolvingContext.QuestionRecord.Name}] with [{resolvingContext.DnsServer}] in {stopWatch.ElapsedMilliseconds} ms");
                    return;
                }
                catch (Exception e)
                {
                    stopWatch.Stop();
                    Console.WriteLine(
                        $"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] failed to resolve [{resolvingContext.QuestionRecord.Name}] with [{resolvingContext.DnsServer}] in {stopWatch.ElapsedMilliseconds} ms, with exception {e.ToString()}");
                    return;
                }
                stopWatch.Stop();
                Console.WriteLine(
                    $"FastResolver[{this.GetHashCode()}-{Thread.CurrentThread.ManagedThreadId}] finish resolving [{resolvingContext.QuestionRecord.Name}] by [{resolvingContext.DnsServer}] in {stopWatch.ElapsedMilliseconds} ms");
            }
        }

        private class ResolvingContext
        {
            public NameServerAddress DnsServer { get; set; }
            public DnsQuestionRecord QuestionRecord { get; set; }
            public DnsResolverConfig DnsResolverConfig { get; set; }
        }

        private class Response
        {
            public DnsDatagram DnsResponse { get; set; }
            public NameServerAddress DnsServer { get; set; }
        }
    }
    
    
}