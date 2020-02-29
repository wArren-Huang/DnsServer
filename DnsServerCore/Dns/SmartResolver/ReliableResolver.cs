using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.Dns.SmartResolver.Analysis;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.SmartResolver
{
    public class ReliableResolver
    {
        private const int SpeedTestTimeout = 2000;
        private const int Concurrency = 500;
        private const int MaxSpeedTestRounds = 3;
        private const long SlowThresholdAbsolute = 200L;
        private const double SlowThresholdCompareToStdDev = 2;

        private readonly DnsResolverConfig DnsResolverConfig;

        private ReliableResolver(DnsResolverConfig dnsResolverConfig)
        {
            DnsResolverConfig = dnsResolverConfig;
        }
        
        private void WriteLog(string message)
        {
            DnsResolverConfig.Log?.Write(message);
        }

        public static DnsDatagram Resolve(DnsQuestionRecord questionRecord, DnsResolverConfig dnsResolverConfig)
        {
            var reliableResolver = new ReliableResolver(dnsResolverConfig);
            return reliableResolver.ResolveAndReturnFastestReliable(questionRecord);
        }
        
        private DnsDatagram ResolveAndReturnFastestReliable(DnsQuestionRecord questionRecord)
        {
            var results = new ConcurrentBag<ResponseResult>();
            string domain = questionRecord.Name;
            var overallStopwatch = new Stopwatch();
            var analysisStopwatch = new Stopwatch();
            var speedTestRounds = ResolvingPreference.IsKnown(domain) ? MaxSpeedTestRounds : 1;
            
            var resolvingTime = new ConcurrentDictionary<string, long>();
            
            ThreadPool.SetMinThreads(Concurrency, Concurrency);
            ThreadPool.SetMaxThreads(Concurrency, Concurrency);
            
            Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                              $"Start to resolve [{domain}]");
            overallStopwatch.Start();
            Parallel.ForEach(DnsResolverConfig.Forwarders, (forwarder) =>
            {
                if (ResolvingPreference.IsBlockedForBeingSlow(forwarder))
                {
                    Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                      $"Skipped resolving [{domain}] from [{forwarder}] as it is marked for being slow");
                    return;
                }
                if (ResolvingPreference.IsBlockedForBeingRouge(domain, forwarder))
                {
                    Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                      $"Kkipped resolving [{domain}] from [{forwarder}] as it is marked for being rouge");
                    return;
                }
                var dnsServerString = forwarder.ToString();
                if (string.IsNullOrEmpty(dnsServerString))
                {
                    return;
                }
                var resolvingStopwatch = new Stopwatch();
                resolvingStopwatch.Start();
                var response = FastResolver.Resolve(questionRecord, forwarder, DnsResolverConfig);
                resolvingStopwatch.Stop();
                Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"Finish resolving [{domain}] by [{dnsServerString}] " +
                                  $"in {resolvingStopwatch.ElapsedMilliseconds.ToString()} ms");
                resolvingTime[dnsServerString] = resolvingStopwatch.ElapsedMilliseconds;
                if (response != null)
                { 
                    Parallel.ForEach(response.Answer, (record) =>
                    {
                        if (record.RDLENGTH != "4 bytes")
                        {
                            return;
                        }
                        var ipAddress = record.RDATA.ToString();
                        if (string.IsNullOrEmpty(ipAddress))
                        {
                            return;
                        }
                        var preferredMethod = ResolvingPreference.GetPreferredMethod(domain);
                        Parallel.Invoke(
                            () =>
                            {
                                if (preferredMethod == SpeedTestMethod.Https ||
                                    preferredMethod == SpeedTestMethod.Unspecified)
                                {
                                    Parallel.For(0, speedTestRounds, (i, loopState) =>
                                    {
                                        var speedTestStopwatch = new Stopwatch();
                                        speedTestStopwatch.Start();
                                        var result = HttpsSpeedTester.Test(ipAddress, domain, SpeedTestTimeout);
                                        speedTestStopwatch.Stop();
                                        Console.WriteLine(
                                            $"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                            $"Finish HTTPS speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds.ToString()} ms");
                                        result.DnsServer = dnsServerString;
                                        result.DnsResponse = response;
                                        result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                        results.Add(result);
                                        if (result.Result != SpeedTestResult.SuccessWithValidation)
                                        {
                                            loopState.Stop();
                                        }
                                    });
                                }
                            },
                            () =>
                            {
                                if (preferredMethod == SpeedTestMethod.Http ||
                                    preferredMethod == SpeedTestMethod.Unspecified)
                                {
                                    Parallel.For(0, speedTestRounds, (i, loopState) =>
                                    {
                                        var speedTestStopwatch = new Stopwatch();
                                        speedTestStopwatch.Start();
                                        var result = HttpSpeedTester.Test(ipAddress, SpeedTestTimeout);
                                        speedTestStopwatch.Stop();
                                        Console.WriteLine(
                                            $"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                            $"Finish HTTP speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds.ToString()} ms");
                                        result.DnsServer = dnsServerString;
                                        result.DnsResponse = response;
                                        result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                        results.Add(result);
                                        if (result.Result != SpeedTestResult.SuccessWithoutValidation)
                                        {
                                            loopState.Stop();
                                        }
                                    });
                                }
                            },
                            () =>
                            {
                                if (preferredMethod == SpeedTestMethod.Ping ||
                                    preferredMethod == SpeedTestMethod.Unspecified)
                                {
                                    Parallel.For(0, speedTestRounds, (i, loopState) =>
                                    {
                                        var speedTestStopwatch = new Stopwatch();
                                        speedTestStopwatch.Start();
                                        var result = PingSpeedTester.Test(ipAddress, SpeedTestTimeout);
                                        speedTestStopwatch.Stop();
                                        Console.WriteLine(
                                            $"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                            $"Finish Ping speed test to [{ipAddress}] in {speedTestStopwatch.ElapsedMilliseconds.ToString()} ms");
                                        result.DnsServer = dnsServerString;
                                        result.DnsResponse = response;
                                        result.ThreadId = Thread.CurrentThread.ManagedThreadId;
                                        results.Add(result);
                                        if (result.Result != SpeedTestResult.SuccessWithoutValidation)
                                        {
                                            loopState.Stop();
                                        }
                                    });
                                }
                            }
                        );
                    });
                }
            });
            Console.WriteLine($"Speed test and validation results for domain [{domain}]:");
            Console.WriteLine(ResponseResult.PrintableHeader);
            foreach (var result in results)
            {
                Console.WriteLine(result.ToString());
            }
            Console.WriteLine();
            Console.WriteLine(
                $"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Start to analysis speed testing results for [{domain}]"); 
            
            analysisStopwatch.Start();
            if (results.GroupBy(r => r.Method).Count() > 1)
            {
                var preferredMethod = GetPreferredSpeedTestMethod(results);
                ResolvingPreference.SetPreferredMethod(domain, preferredMethod);
            }
            var slowServers = FindSlowServers(resolvingTime);
            foreach (var slowServer in slowServers)
            {
                ResolvingPreference.MarkAsBlockedForBeingSlow(slowServer);
            }
            var bestResult = PickBestResponse(domain, results);
            analysisStopwatch.Stop();
            Console.WriteLine(
                $"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                $"Finish analyzing speed testing results for [{domain}] in {analysisStopwatch.ElapsedMilliseconds.ToString()} ms"); 
            
            overallStopwatch.Stop();
            Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                              $"Finish reliable resolving best response from [{bestResult.DnsServer}] for domain [{domain}] " +
                              $"in {overallStopwatch.ElapsedMilliseconds.ToString()} ms");
            Console.WriteLine();
            return bestResult.DnsResponse;
        }

        private static SpeedTestMethod GetPreferredSpeedTestMethod(ConcurrentBag<ResponseResult> results)
        {
            if (results.Any(r =>
                r.Method == SpeedTestMethod.Ping &&
                r.Result == SpeedTestResult.SuccessWithoutValidation))
            {
                return SpeedTestMethod.Ping;
            }

            if (results.Any(r =>
                r.Method == SpeedTestMethod.Https &&
                r.Result == SpeedTestResult.SuccessWithValidation))
            {
                return SpeedTestMethod.Https;
            }

            if (results.Any(r =>
                r.Method == SpeedTestMethod.Http &&
                r.Result == SpeedTestResult.SuccessWithoutValidation))
            {
                return SpeedTestMethod.Http;
            }

            return SpeedTestMethod.SkipTest;
        }
        
        private ResponseResult PickBestResponse(string domain, ConcurrentBag<ResponseResult> results)
        {
            if (results.Count <= 0)
            {
                return new ResponseResult();
            }

            var validatedResponses = results.Where(r =>
                r.Method == SpeedTestMethod.Https && 
                r.Result == SpeedTestResult.SuccessWithValidation).ToArray();
            if (validatedResponses.Length > 0)
            {
                var rougeResponses = results.Where(r => 
                    r.Method == SpeedTestMethod.Https && 
                    r.Result != SpeedTestResult.SuccessWithValidation).ToArray();
                foreach (var rougeResponse in rougeResponses)
                {
                    ResolvingPreference.MarkAsBlockedForBeingRouge(domain, rougeResponse.DnsServer);
                }
                Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"HTTPS performance:");
                var groupPerformance = AnalysisPerformance(validatedResponses, SpeedTestMethod.Https);
                var minAverage = groupPerformance.Min(p => p.Average);
                Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"Min Average:{minAverage.ToString()}");
                return groupPerformance.Where(g => g.Average == minAverage).FirstOrDefault().ResponseResult;
            }

            var successResponses = results.Where(r =>
                r.Result == SpeedTestResult.SuccessWithoutValidation).ToArray();
            if (successResponses.Length > 0)
            {
                Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"Ping performance:");
                var groupPerformance = AnalysisPerformance(successResponses, SpeedTestMethod.Ping);
                if (groupPerformance.Length > 0)
                {
                    var minAverage = groupPerformance.Min(p => p.Average);
                    Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                      $"Min Average:{minAverage.ToString()}");
                    return groupPerformance.Where(g => g.Average == minAverage).FirstOrDefault().ResponseResult;
                }

                Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"HTTP performance:");
                groupPerformance = AnalysisPerformance(successResponses, SpeedTestMethod.Http);
                if (groupPerformance.Length > 0)
                {
                    var minAverage = groupPerformance.Min(p => p.Average);
                    Console.WriteLine($"ReliableResolver[{GetHashCode().ToString()}-{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                      $"Min Average:{minAverage.ToString()}");
                    return groupPerformance.Where(g => g.Average == minAverage).FirstOrDefault().ResponseResult;
                }
                return new ResponseResult();
            }

            return results.FirstOrDefault();
        }
        
        private static ResponsePerformance[] AnalysisPerformance(IEnumerable<ResponseResult> results, SpeedTestMethod method)
        {
            var groupPerformance = results
                .Where(r => r.Method == method)
                .GroupBy(r => r.DnsServer)
                .Select(g => new ResponsePerformance()
                {
                    By = g.Key,
                    ResponseResult = g.FirstOrDefault(),
                    Max = g.Max(r => r.Time),
                    Average = g.Average(r => r.Time),
                    Min = g.Min(r => r.Time)
                }).ToArray();

            foreach (var responsePerformance in groupPerformance)
            {
                Console.WriteLine(responsePerformance.ToString());
            }
            Console.WriteLine();
            return groupPerformance;
        }

        private static string[] FindSlowServers(ConcurrentDictionary<string, long> resolvingTime)
        {
            if (resolvingTime.Count <= 3)
            {
                return new string[] {};
            }

            var stdDev = StandardDeviation(resolvingTime.Values);

            return resolvingTime
                .Where(r =>
                    Math.Abs(r.Value - stdDev.Average) > (SlowThresholdCompareToStdDev * stdDev.Deviation) &&
                    r.Value > SlowThresholdAbsolute)
                .Select(r => r.Key)
                .ToArray();
        }
        
        private static DeviationStatistics StandardDeviation(IEnumerable<long> enumerableLongs)
        {
            var values = enumerableLongs as long[] ?? enumerableLongs.ToArray();
            if (values.Length < 1)
            {
                return new DeviationStatistics()
                {
                    Deviation = 0,
                    Average = 0
                };
            }
            if (values.Length == 1)
            {
                return new DeviationStatistics()
                {
                    Deviation = 0,
                    Average = values[0]
                };
            }
            var average = values.Average();
            var sum = values.Sum(d => (d - average) * (d - average));
            var deviation = Math.Sqrt(sum / values.Length );
            return new DeviationStatistics()
            {
                Deviation = deviation,
                Average = average
            };
        }

        private struct DeviationStatistics
        {
            public double Deviation { get; set; }
            public double Average { get; set; }
        }
    }
}