using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DnsServerCore.Dns.SmartResolver;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.AntiRogue
{
    public static class AntiRogueResolver
    {
        private const string Splitter = ",";
        private const int HttpsPort = 443;
        private const int TestTimeout = 5000;
        
        private static readonly TimeSpan ExpireRogueAfter;
        private static readonly TimeSpan ExpireNonRogueAfter;
        private static readonly TimeSpan ExpireCannotDeterminAfter;
        private static readonly TimeSpan ExpireTestingStatusAfter;
        
        private static readonly ConcurrentDictionary<string, Expiration> Rogues;
        private static readonly ConcurrentDictionary<string, Expiration> NonRogues;
        private static readonly ConcurrentDictionary<string, Expiration> CannotDetermine;
        private static readonly ConcurrentDictionary<string, Expiration> Testings;
        private static readonly ConcurrentDictionary<ConcurrentDictionary<string, Expiration>, UpdateStatus> UpdateAndSaveTime;
        
        static AntiRogueResolver()
        {
            ExpireRogueAfter = TimeSpan.FromDays(180);
            ExpireNonRogueAfter = TimeSpan.FromDays(1);
            ExpireCannotDeterminAfter = TimeSpan.FromDays(1);
            ExpireTestingStatusAfter = TimeSpan.FromMinutes(1);
            
            Rogues = new ConcurrentDictionary<string, Expiration>();
            NonRogues = new ConcurrentDictionary<string, Expiration>();
            CannotDetermine = new ConcurrentDictionary<string, Expiration>();
            Testings = new ConcurrentDictionary<string, Expiration>();
            UpdateAndSaveTime = new ConcurrentDictionary<ConcurrentDictionary<string, Expiration>, UpdateStatus>
            {
                [Rogues] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now},
                [NonRogues] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now},
                [CannotDetermine] = new UpdateStatus() {UpdatedAt = DateTime.Now, SavedAt = DateTime.Now}
            };
            LoadFromFiles();
            var fileSavingInterval = TimeSpan.FromMinutes(1);
            var fileSavingTimer = new Timer(new TimerCallback((state) =>
            {
                SaveToFiles();
            }), null, fileSavingInterval, fileSavingInterval);
        }
        private static void PrepareConfigFiles(out string RogueFile, out string NonRogueFile, out string CannotDetermineFile)
        {
            var appDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var configDirectory = Path.Combine(appDirectory, "config");
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            RogueFile = Path.Combine(configDirectory, "rogues");
            NonRogueFile = Path.Combine(configDirectory, "non-rogues");
            CannotDetermineFile = Path.Combine(configDirectory, "cannot-determine");
        }

        private static void LoadFromFiles()
        {
            PrepareConfigFiles(
                out var rogueFile, 
                out var nonRogueFile, 
                out var cannotDetermineFile);
            LoadFromFile(rogueFile, Rogues, "rogue");
            LoadFromFile(nonRogueFile, NonRogues, "non-rogue");
            LoadFromFile(cannotDetermineFile, CannotDetermine, "cannot-determine");
        }
        private static void LoadFromFile(string file, ConcurrentDictionary<string, Expiration> dictionary, string name)
        {
            if (File.Exists(file))
            {
                using (var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    var reader = new StreamReader(fileStream, Encoding.UTF8);
                    var parsingLine = 0;
                    var parsedRecords = 0;
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        parsingLine++;
                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }
                        try
                        {
                            var parts = line.Split(new[] {Splitter}, StringSplitOptions.RemoveEmptyEntries);
                            var domain = parts[0];
                            var expires = DateTime.FromFileTime(long.Parse(parts[1]));
                            dictionary[domain] = new Expiration() {ExpiresAt = expires};
                            parsedRecords++;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(
                                value: $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                                       $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                                       $"Failed to parse line {parsingLine} from {file}: [{line}], possible incorrect format.");
                            Console.WriteLine(e);
                        }
                    }
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Loaded {parsedRecords} {name} records from {file}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"{file} not exist, skipped loading {name} records");
            }
        }
        private static void SaveToFiles()
        {
            PrepareConfigFiles(
                out var rogueFile, 
                out var nonRogueFile, 
                out var cannotDetermineFile);
            SaveToFile(Rogues, rogueFile, "rogue");
            SaveToFile(NonRogues, nonRogueFile, "non-rogues");
            SaveToFile(CannotDetermine, cannotDetermineFile, "cannot-determine");
        }
        private static void SaveToFile(ConcurrentDictionary<string, Expiration> dictionary, string file, string name)
        {
            if (UpdateAndSaveTime[dictionary].IsDirty)
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Start saving {name} records to {file} ...");
                using (var memoryStream = new MemoryStream())
                {
                    var writer = new StreamWriter(memoryStream, Encoding.UTF8);
                    foreach (var record in dictionary)
                    {
                        var domain = record.Key;
                        var expires = record.Value.ExpiresAt.ToFileTime().ToString();
                        writer.WriteLine($"{domain}{Splitter}{expires}");
                    }
                    writer.Flush();
                    memoryStream.Position = 0;
                    using (var fileStream = new FileStream(file, FileMode.Create, FileAccess.Write))
                    {
                        memoryStream.CopyTo(fileStream);
                        fileStream.Flush();
                    }
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Saved {name} records to {file}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                    $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                    $"Skipped updating {file} as {name} records not changed");
            }
        }

        public static void StartToTestDomain(DnsQuestionRecord dnsQuestion, NameServerAddress[] forwarders, 
            DnsResolverConfig dnsResolvingConfig)
        {
            var domain = dnsQuestion.Name;
            var testResult = GetRogueResult(domain);
            switch (testResult)
            {
                case RogueResult.Rogue:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as being rogue and will not be tested before " +
                        $"[{Rogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.NotRogue:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as not rogue and will not be tested before " +
                        $"[{NonRogues[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.CannotDetermine:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is marked as cannot determined and will not be tested before " +
                        $"[{CannotDetermine[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
                case RogueResult.Testing:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"[{domain}] is still being tested and is expected to finish by " +
                        $"[{Testings[domain].ExpiresAt.ToString(CultureInfo.CurrentCulture)}]");
                    return;
            }
            Task.Run(() =>
            {
                Test(dnsQuestion, forwarders, dnsResolvingConfig);
            });
            MarkDomain(domain, RogueResult.Testing);
        }

        private static void Test(DnsQuestionRecord questionRecord, NameServerAddress[] forwarders,
            DnsResolverConfig dnsResolvingConfig)
        {
            var domain = questionRecord.Name;
            var results = new LinkedList<HttpsTestResult>();
            foreach (var forwarder in forwarders)
            {
                var dnsServerString = forwarder.ToString();
                if (string.IsNullOrEmpty(dnsServerString))
                {
                    continue;
                }
                var response = FastResolver.Resolve(questionRecord, forwarder, dnsResolvingConfig);
                if (response == null)
                {
                    continue;
                }
                foreach (var record in response.Answer)
                {
                    if (record.RDLENGTH != "4 bytes")
                    {
                        continue;
                    }
                    var ipAddress = record.RDATA.ToString();
                    if (string.IsNullOrEmpty(ipAddress))
                    {
                        continue;
                    }
                    var tcpClient = new TcpClient();
                    var result = tcpClient.BeginConnect(ipAddress, HttpsPort, null, null);
                    var waitHandle = result.AsyncWaitHandle;
                    var success = waitHandle.WaitOne(TimeSpan.FromMilliseconds(TestTimeout), false);
                    if (!success)
                    {
                        results.Append(HttpsTestResult.FailedToConnect);
                    }
                    try
                    {
                        tcpClient.EndConnect(result);
                    }
                    catch (Exception)
                    {
                        results.Append(HttpsTestResult.FailedToConnect);
                    }
                    try
                    {
                        var sslStream = new SslStream(
                            tcpClient.GetStream(),
                            false,
                            (o, certificate, chain, errors) => errors == SslPolicyErrors.None,
                            null);
                        sslStream.AuthenticateAsClient(domain);
                        results.Append(HttpsTestResult.AuthenticationSuccess);
                    }
                    catch (AuthenticationException)
                    {
                        results.Append(HttpsTestResult.AuthenticationFailed);
                    }
                    finally
                    {
                        tcpClient.Close();
                    }
                }
            }

            var resultsArray = results.ToArray();
            if (resultsArray.Length == 0)
            {
                MarkDomain(domain, RogueResult.CannotDetermine);
            }
            else if (resultsArray.Any(r => r == HttpsTestResult.AuthenticationSuccess))
            {
                if (resultsArray.Any(r => r == HttpsTestResult.AuthenticationFailed) ||
                    resultsArray.Any(r => r == HttpsTestResult.FailedToConnect))
                {
                    MarkDomain(domain, RogueResult.Rogue);
                }
                else
                {
                    MarkDomain(domain, RogueResult.NotRogue);
                }
            }
            else
            {
                MarkDomain(domain, RogueResult.CannotDetermine);
            }
        }

        private static void MarkDomain(string domain, RogueResult result)
        {
            switch (result)
            {
                case RogueResult.Rogue:
                    if (Rogues.ContainsKey(domain))
                    {
                        lock (Rogues[domain])
                        {
                            Rogues[domain].ExpiresAt = DateTime.Now.Add(ExpireRogueAfter);
                        }
                        lock (UpdateAndSaveTime[Rogues])
                        {
                            UpdateAndSaveTime[Rogues].UpdatedAt = DateTime.Now;
                        }
                    }
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.NotRogue:
                    if (NonRogues.ContainsKey(domain))
                    {
                        lock (NonRogues[domain])
                        {
                            NonRogues[domain].ExpiresAt = DateTime.Now.Add(ExpireNonRogueAfter);
                        }
                        lock (UpdateAndSaveTime[NonRogues])
                        {
                            UpdateAndSaveTime[NonRogues].UpdatedAt = DateTime.Now;
                        }
                    }
                    RemoveRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.CannotDetermine:
                    if (CannotDetermine.ContainsKey(domain))
                    {
                        lock (CannotDetermine[domain])
                        {
                            CannotDetermine[domain].ExpiresAt = DateTime.Now.Add(ExpireNonRogueAfter);
                        }
                        lock (UpdateAndSaveTime[CannotDetermine])
                        {
                            UpdateAndSaveTime[CannotDetermine].UpdatedAt = DateTime.Now;
                        }
                    }
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                case RogueResult.Testing:
                    if (Testings.ContainsKey(domain))
                    {
                        lock (Testings[domain])
                        {
                            Testings[domain].ExpiresAt = DateTime.Now.Add(ExpireNonRogueAfter);
                        }
                        lock (UpdateAndSaveTime[Testings])
                        {
                            UpdateAndSaveTime[Testings].UpdatedAt = DateTime.Now;
                        }
                    }
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    break;
                case RogueResult.NotTested:
                    RemoveRogueRecord(domain);
                    RemoveNonRogueRecord(domain);
                    RemoveCannotDetermineRecord(domain);
                    RemoveTestingRecord(domain);
                    break;
                default:
                    Console.WriteLine(
                        $"{DateTime.Now.ToString("g", CultureInfo.CurrentCulture)} " +
                        $"AntiRogueResolver[{Thread.CurrentThread.ManagedThreadId.ToString()}] " +
                        $"Cannot make [{domain}] as [{result.ToString()}], need to fix MarkDomain method");
                    break;
            }
        }
        
        public static RogueResult GetRogueResult(string domain)
        {
            if (Rogues.ContainsKey(domain))
            {
                if (Rogues[domain].IsNotExpired)
                {
                    return RogueResult.Rogue;
                }
                RemoveRogueRecord(domain);
            }

            if (NonRogues.ContainsKey(domain))
            {
                if (NonRogues[domain].IsNotExpired)
                {
                    return RogueResult.NotRogue;
                }
                RemoveNonRogueRecord(domain);
            }

            if (CannotDetermine.ContainsKey(domain))
            {
                if (CannotDetermine[domain].IsNotExpired)
                {
                    return RogueResult.CannotDetermine;
                }
                RemoveCannotDetermineRecord(domain);
            }

            if (Testings.ContainsKey(domain))
            {
                if (Testings[domain].IsNotExpired)
                {
                    return RogueResult.Testing;
                }
                RemoveTestingRecord(domain);
            }

            return RogueResult.NotTested;
        }
        
        private static void RemoveRogueRecord(string domain)
        {
            if (!Rogues.ContainsKey(domain)) return;
            Rogues.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[Rogues])
            {
                UpdateAndSaveTime[Rogues].UpdatedAt = DateTime.Now;
            }
        }
        private static void RemoveNonRogueRecord(string domain)
        {
            if (!NonRogues.ContainsKey(domain)) return;
            NonRogues.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[NonRogues])
            {
                UpdateAndSaveTime[NonRogues].UpdatedAt = DateTime.Now;
            }
        }
        private static void RemoveCannotDetermineRecord(string domain)
        {
            if (!CannotDetermine.ContainsKey(domain)) return;
            CannotDetermine.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[CannotDetermine])
            {
                UpdateAndSaveTime[CannotDetermine].UpdatedAt = DateTime.Now;
            }
        }
        private static void RemoveTestingRecord(string domain)
        {
            if (!Testings.ContainsKey(domain)) return;
            Testings.TryRemove(domain, out var expired);
            lock (UpdateAndSaveTime[Testings])
            {
                UpdateAndSaveTime[Testings].UpdatedAt = DateTime.Now;
            }
        }
    }
}