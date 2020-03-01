using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.IO;
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

        private const string StartRouge = "========== Start Rouge ==========";
        private const string EndRouge = "========== End Rouge ==========";
        private const string StartSlow = "========== Start Slow ==========";
        private const string EndSlow = "========== End Slow ==========";
        private const string StartKnown = "========== Start Known ==========";
        private const string EndKnown = "========== End Known ==========";
        private const string StartPreferMethod = "========== Start Prefer ==========";
        private const string EndPreferMethod = "========== End Prefer ==========";
        private const string StartUpdatedAt = "========== Start Updated ==========";
        private const string EndUpdatedAt = "========== End Updated ==========";
        private const string StartSavedAt = "========== Start Saved ==========";
        private const string EndSavedAt = "========== End Saved ==========";
        private const string Pfs = "|";
        
        private static readonly char[] DomainSplitterCharArray;
        private static readonly object TimeLock;
        private static readonly string PreferenceFile;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> RougeServers;
        private static readonly ConcurrentDictionary<string, DateTime> SlowServers;
        private static readonly ConcurrentDictionary<string, DateTime> KnownDomains;
        private static readonly ConcurrentDictionary<string, PreferMethodRecord> DomainPreferredMethod;

        private static readonly TimeSpan BlockRougeServerFor;
        private static readonly TimeSpan BlockSlowServerFor;
        private static readonly TimeSpan ExpirePreferMethodAfter;
        private static readonly TimeSpan ForgetDomainAfter;
        private static readonly TimeSpan CheckPreferenceForSavingInterval;
        
        private static DateTime _lastUpdate;
        private static DateTime _lastSave;

        private static readonly Task SavingTask;

        static ResolvingPreference()
        {
            BlockRougeServerFor = TimeSpan.FromDays(90);
            BlockSlowServerFor = TimeSpan.FromMinutes(1);
            ExpirePreferMethodAfter = TimeSpan.FromDays(1);
            ForgetDomainAfter = TimeSpan.FromMinutes(59);
            CheckPreferenceForSavingInterval = TimeSpan.FromMinutes(1);
            
            DomainSplitterCharArray = new[] {'.'};
            TimeLock = new object();

            var appDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var configDirectory = Path.Combine(appDirectory, "config");
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }
            PreferenceFile = Path.Combine(configDirectory, "preference");

            RougeServers = 
                new ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>>(Concurrency, TargetDomainCount);
            SlowServers = 
                new ConcurrentDictionary<string, DateTime>(Concurrency, DnsServerCount);
            DomainPreferredMethod = 
                new ConcurrentDictionary<string, PreferMethodRecord>(Concurrency, TargetDomainCount);
            KnownDomains = 
                new ConcurrentDictionary<string, DateTime>();

            lock (TimeLock)
            {
                _lastUpdate = DateTime.Now;
                _lastSave = DateTime.Now.AddMilliseconds(1);
            }
            
            LoadFromFile();
            
            SavingTask = Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    Thread.Sleep((int) CheckPreferenceForSavingInterval.TotalMilliseconds);
                    SaveToFile();
                }
            }, TaskCreationOptions.LongRunning);
        }

        private static bool IsDirty()
        {
            return _lastSave.CompareTo(_lastUpdate) < 0;
        }
        
        private static void LoadFromFile()
        {
            if (!File.Exists(PreferenceFile))
            {
                Console.WriteLine(
                    $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Preference not exist, applying empty preferences.");
                return;
            }

            try
            {
                using (var fileStream = new FileStream(PreferenceFile, FileMode.Open, FileAccess.Read))
                {
                    var reader = new StreamReader(fileStream, Encoding.UTF8);
                    while (!reader.EndOfStream)
                    {
                        string line;
                        switch (reader.ReadLine())
                        {
                            case StartRouge:
                                line = reader.ReadLine();
                                do
                                {
                                    var parts = line.Split(new[] {Pfs}, StringSplitOptions.RemoveEmptyEntries);
                                    var domain = parts[0];
                                    var dnsServer = parts[1];
                                    var since = DateTime.FromFileTime(long.Parse(parts[2]));
                                    if (!RougeServers.ContainsKey(parts[0]))
                                    {
                                        RougeServers[domain] =
                                            new ConcurrentDictionary<string, DateTime>(Concurrency, TargetDomainCount);
                                    }

                                    RougeServers[domain][dnsServer] = since;
                                    line = reader.ReadLine();
                                } while (EndRouge.Equals(line));
                                break;
                            case StartSlow:
                                line = reader.ReadLine();
                                do
                                {
                                    var parts = line.Split(new[] {Pfs}, StringSplitOptions.RemoveEmptyEntries);
                                    var dnsServer = parts[0];
                                    var since = DateTime.FromFileTime(long.Parse(parts[1]));
                                    SlowServers[dnsServer] = since;
                                    line = reader.ReadLine();
                                } while (EndSlow.Equals(line));
                                break;
                            case StartKnown:
                                line = reader.ReadLine();
                                do
                                {
                                    var parts = line.Split(new[] {Pfs}, StringSplitOptions.RemoveEmptyEntries);
                                    var domain = parts[0];
                                    var since = DateTime.FromFileTime(long.Parse(parts[1]));
                                    KnownDomains[domain] = since;
                                    line = reader.ReadLine();
                                } while (EndKnown.Equals(line));
                                break;
                            case StartSavedAt:
                                line = reader.ReadLine();
                                do
                                {
                                    _lastSave = DateTime.FromFileTime(long.Parse(line));
                                    line = reader.ReadLine();
                                } while (EndSavedAt.Equals(line));
                                break;
                            case StartUpdatedAt:
                                line = reader.ReadLine();
                                do
                                {
                                    _lastUpdate = DateTime.FromFileTime(long.Parse(line));
                                    line = reader.ReadLine();
                                } while (EndUpdatedAt.Equals(line));
                                break;
                            case StartPreferMethod:
                                line = reader.ReadLine();
                                do
                                {
                                    var parts = line.Split(new[] {Pfs}, StringSplitOptions.RemoveEmptyEntries);
                                    var domain = parts[0];
                                    var method = (SpeedTestMethod) Enum.Parse(typeof(SpeedTestMethod), parts[1]);
                                    var since = DateTime.FromFileTime(long.Parse(parts[2]));
                                    DomainPreferredMethod[domain] = new PreferMethodRecord()
                                    {
                                        Method = method,
                                        StartTime = since
                                    };
                                    line = reader.ReadLine();
                                } while (EndPreferMethod.Equals(line));
                                break;
                            case null:
                                break;
                            default:
                                throw new InvalidDataException("Incorrect Preference file format");
                        }
                    }

                    Console.WriteLine(
                        $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Preference file loaded.");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                  $"Failed to load from Preference file." +
                                  e.ToString());
            }
        }

        private static void SaveToFile()
        {
            if (!IsDirty())
            {
                Console.WriteLine($"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Preference is not updated in the last {CheckPreferenceForSavingInterval.Minutes.ToString()} minutes " +
                    $"thus does not need to be saved.");
                return;
            }

            using (var memoryStream = new MemoryStream())
            {
                var writer = new StreamWriter(memoryStream, Encoding.UTF8);
                
                writer.WriteLine(StartPreferMethod);
                foreach (var domainRecord in DomainPreferredMethod)
                {
                    var domain = domainRecord.Key;
                    var method = domainRecord.Value.Method;
                    var since = domainRecord.Value.StartTime;
                    writer.WriteLine($"{domain}{Pfs}{method.ToString()}{Pfs}{since.ToFileTime().ToString()}");
                }
                writer.WriteLine(EndPreferMethod);
                
                writer.WriteLine(StartRouge);
                foreach (var domainRecord in RougeServers)
                {
                    var domain = domainRecord.Key;
                    foreach (var serverRecord in domainRecord.Value)
                    {
                        writer.WriteLine(
                            $"{domain}{Pfs}{serverRecord.Key}{Pfs}{serverRecord.Value.ToFileTime().ToString()}");
                    }
                }
                writer.WriteLine(EndRouge);

                writer.WriteLine(StartSlow);
                foreach (var slowServer in SlowServers)
                {
                    writer.WriteLine($"{slowServer.Key}{Pfs}{slowServer.Value.ToFileTime().ToString()}");
                }
                writer.WriteLine(EndSlow);

                writer.WriteLine(StartKnown);
                foreach (var knownDomain in KnownDomains)
                {
                    writer.WriteLine($"{knownDomain.Key}{Pfs}{knownDomain.Value.ToFileTime().ToString()}");
                }
                writer.WriteLine(EndKnown);

                lock (TimeLock)
                {
                    _lastSave = DateTime.Now;
                    writer.WriteLine(StartUpdatedAt);
                    writer.WriteLine(_lastUpdate.ToFileTime().ToString());
                    writer.WriteLine(EndUpdatedAt);
                }

                writer.WriteLine(StartSavedAt);
                writer.WriteLine(_lastSave.ToFileTime().ToString());
                writer.WriteLine(EndSavedAt);

                writer.Flush();
                using (var fileStream = new FileStream(PreferenceFile, FileMode.Create, FileAccess.Write))
                {
                    memoryStream.Position = 0;
                    memoryStream.CopyTo(fileStream);
                    fileStream.Flush();
                }

                Console.WriteLine(
                    $"ResolvingPreference[{Thread.CurrentThread.ManagedThreadId.ToString()}] Preference saved.");
            }
        }

        private static void MarkUpdated()
        {
            lock (TimeLock)
            {
                _lastUpdate = DateTime.Now;
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
                KnownDomains[fqdn] = DateTime.Now;
                MarkUpdated();
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