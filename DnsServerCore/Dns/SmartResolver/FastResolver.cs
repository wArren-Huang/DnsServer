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
        private ManualResetEvent _ManualResetEvent = null;
        private Thread[] _ResolvingThreads = null;
        private Response _Response = new Response();
        
        public static DnsDatagram Resolve(DnsQuestionRecord questionRecord, NameServerAddress dnsServer,
            Config config)
        {
            try
            {
                var dnsClient = new DnsClient(dnsServer)
                {
                    Proxy = config.Proxy,
                    PreferIPv6 = config.PreferIPv6,
                    Protocol = config.ForwarderProtocol,
                    Retries = config.Retries,
                    Timeout = config.Timeout
                };
                return dnsClient.Resolve(questionRecord);
            }
            catch
            {
                return null;
            }
        }

        public DnsDatagram Resolve(DnsQuestionRecord questionRecord, NameServerAddress[] dnsServers,
            Config config)
        {
            var question = questionRecord.Name;
            var overallStopwatch = new Stopwatch();
            _ManualResetEvent = new ManualResetEvent(false);
            _ResolvingThreads = new Thread[dnsServers.Length];

            overallStopwatch.Start();
            Console.WriteLine($"FastResolver[{this.GetHashCode()}] start to resolve [{question}]");
            for (var i = 0; i < dnsServers.Length; i++)
            {
                _ResolvingThreads[i] = new Thread(new ParameterizedThreadStart(ResolveWith));
                var context = new ResolvingContext()
                {
                    Config = config,
                    DnsServer = dnsServers[i],
                    QuestionRecord = questionRecord
                } as object;
                _ResolvingThreads[i].Start(context);
            }
            Console.WriteLine(
                $"FastResolver[{this.GetHashCode()}] finish starting all resolving threads for [{question}] in {overallStopwatch.ElapsedMilliseconds} ms");
            _ManualResetEvent.WaitOne();
            lock (_Response)
            {
                overallStopwatch.Stop();
                Console.WriteLine(
                    $"FastResolver[{this.GetHashCode()}] finish resolving [{question}] by [{_Response.DnsServer}] in {overallStopwatch.ElapsedMilliseconds} ms");
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
                        Proxy = resolvingContext.Config.Proxy,
                        PreferIPv6 = resolvingContext.Config.PreferIPv6,
                        Protocol = resolvingContext.Config.ForwarderProtocol,
                        Retries = resolvingContext.Config.Retries,
                        Timeout = resolvingContext.Config.Timeout
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
            public Config Config { get; set; }
        }

        private class Response
        {
            public DnsDatagram DnsResponse { get; set; }
            public NameServerAddress DnsServer { get; set; }
        }
    }
    
    
}